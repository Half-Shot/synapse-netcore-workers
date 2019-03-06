﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Matrix.SynapseInterop.Common;
using Matrix.SynapseInterop.Database;
using Matrix.SynapseInterop.Database.Models;
using Matrix.SynapseInterop.Replication.Structures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using Serilog;

namespace Matrix.SynapseInterop.Worker.FederationSender
{
    public class TransactionQueue
    {
        private const int MAX_PDUS_PER_TRANSACTION = 50;
        private const int MAX_EDUS_PER_TRANSACTION = 100;
        private static readonly ILogger log = Log.ForContext<TransactionQueue>();
        private readonly Backoff _backoff;
        private readonly FederationClient _client;
        private readonly string _connString;
        private readonly Dictionary<string, long> _destLastDeviceListStreamId;
        private readonly Dictionary<string, long> _destLastDeviceMsgStreamId;
        private readonly Dictionary<string, Task> _destOngoingTrans;
        private readonly ConcurrentDictionary<string, LinkedList<Transaction>> _destPendingTransactions;
        private readonly string _serverName;

        private readonly Dictionary<string, PresenceState> _userPresence;
        private readonly SemaphoreSlim _concurrentTransactionLock;
        private Task _eventsProcessing;
        private int _lastEventPoke;
        private Task _presenceProcessing;
        private SigningKey _signingKey;
        private int _txnId;

        public TransactionQueue(string serverName,
                                string connectionString,
                                SigningKey key,
                                IConfigurationSection clientConfig
        )
        {
            _client = new FederationClient(serverName, key, clientConfig);
            _userPresence = new Dictionary<string, PresenceState>();
            _destOngoingTrans = new Dictionary<string, Task>();
            _destPendingTransactions = new ConcurrentDictionary<string, LinkedList<Transaction>>();
            _destLastDeviceMsgStreamId = new Dictionary<string, long>();
            _destLastDeviceListStreamId = new Dictionary<string, long>();
            _presenceProcessing = Task.CompletedTask;
            _eventsProcessing = Task.CompletedTask;
            _serverName = serverName;
            _txnId = (int) (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
            _connString = connectionString;
            _lastEventPoke = -1;
            _signingKey = key;
            _backoff = new Backoff();
            var txConcurrency = clientConfig.GetValue<int>("maxConcurrency");

            if (txConcurrency == 0) txConcurrency = 100;

            _concurrentTransactionLock = new SemaphoreSlim(txConcurrency, txConcurrency);
        }

        public void OnEventUpdate(string streamPos)
        {
            _lastEventPoke = Math.Max(int.Parse(streamPos), _lastEventPoke);

            if (!_eventsProcessing.IsCompleted) return;

            log.Debug("Poking ProcessPendingEvents");

            _eventsProcessing = ProcessPendingEvents().ContinueWith(t =>
            {
                if (t.IsFaulted) log.Error("Failed to process events: {Exception}", t.Exception);
            });
        }

        public void SendPresence(List<PresenceState> presenceSet)
        {
            foreach (var presence in presenceSet)
                // Only send presence about our own users.
                if (IsMineId(presence.user_id))
                    if (!_userPresence.TryAdd(presence.user_id, presence))
                    {
                        _userPresence.Remove(presence.user_id);
                        _userPresence.Add(presence.user_id, presence);
                    }

            if (!_presenceProcessing.IsCompleted) return;

            _presenceProcessing = ProcessPendingPresence();
        }

        public void SendEdu(EduEvent obj)
        {
            if (obj.destination == _serverName) return;

            // Prod device messages if we've not seen this destination before.
            if (!_destLastDeviceMsgStreamId.ContainsKey(obj.destination)) SendDeviceMessages(obj.destination);

            var transaction = GetOrCreateTransactionForDest(obj.destination);

            transaction.edus.Add(obj);
            AttemptTransaction(obj.destination);
        }

        public void SendEdu(EduEvent ev, string key)
        {
            var transaction = GetOrCreateTransactionForDest(ev.destination);
            var existingItem = transaction.edus.FindIndex(edu => edu.InternalKey == key);

            if (existingItem >= 0) transaction.edus.RemoveAt(existingItem);

            ev.InternalKey = key;
            SendEdu(ev);
        }

        public void SendDeviceMessages(string destination)
        {
            if (_serverName == destination) return; // Obviously.

            // Fetch messages for destination
            var messages = GetNewDeviceMessages(destination);

            if (messages.Item1.Count == 0) return;

            var transaction = GetOrCreateTransactionForDest(destination);

            messages.Item1.ForEach(message =>
            {
                // If we go over the limit, go to the next transaction
                if (transaction.edus.Count == MAX_EDUS_PER_TRANSACTION)
                {
                    transaction = GetOrCreateTransactionForDest(destination);
                }

                transaction.edus.Add(new EduEvent
                {
                    destination = destination,
                    content = JObject.Parse(message.MessagesJson),
                    edu_type = "m.direct_to_device",
                    origin = _serverName,
                    StreamId = message.StreamId
                });
            });

            messages.Item2.ForEach(list =>
            {
                // If we go over the limit, go to the next transaction
                if (transaction.edus.Count == MAX_EDUS_PER_TRANSACTION)
                {
                    transaction = GetOrCreateTransactionForDest(destination);
                }

                transaction.edus.Add(new EduEvent
                {
                    destination = destination,
                    content = JObject.FromObject(list),
                    edu_type = "m.device_list_update",
                    origin = _serverName,
                    StreamId = list.stream_id
                });
            });

            AttemptTransaction(destination);
        }

        private Tuple<List<DeviceFederationOutbox>, List<DeviceContentSet>> GetNewDeviceMessages(string destination)
        {
            var lastMsgId = _destLastDeviceMsgStreamId.GetValueOrDefault(destination, 0);
            var lastListId = _destLastDeviceListStreamId.GetValueOrDefault(destination, 0);

            using (var db = new SynapseDbContext(_connString))
            {
                var messages = db
                              .DeviceFederationOutboxes
                              .Where(message =>
                                         message.Destination == destination &&
                                         message.StreamId > lastMsgId)
                              .OrderBy(message => message.StreamId).Take(MAX_EDUS_PER_TRANSACTION).ToList();

                var resLists = db.GetNewDevicesForDestination(destination, MAX_EDUS_PER_TRANSACTION);
                return Tuple.Create(messages, resLists);
            }
        }

        private async Task ProcessPendingPresence()
        {
            var presenceSet = _userPresence.Values.ToList();
            _userPresence.Clear();
            var hostsAndState = await GetInterestedRemotes(presenceSet);

            foreach (var hostState in hostsAndState)
            {
                var formattedPresence = FormatPresenceContent(hostState.Value);

                foreach (var host in hostState.Key)
                {
                    log.Debug("Sending presence to {host}", host);
                    var transaction = GetOrCreateTransactionForDest(host);

                    transaction.edus.Add(new EduEvent
                    {
                        destination = host,
                        origin = _serverName,
                        edu_type = "m.presence",
                        content = formattedPresence
                    });
                }
            }

            // Do this seperate from the above to batch presence together
            foreach (var hostState in hostsAndState)
            foreach (var host in hostState.Key)
                AttemptTransaction(host);
        }

        private async Task ProcessPendingEvents()
        {
            List<EventJsonSet> events;
            var top = _lastEventPoke;
            int last;

            // Get the set of events we need to process.
            using (var db = new SynapseDbContext(_connString))
            {
                last = (await db.FederationStreamPosition.SingleAsync(m => m.Type == "events")).StreamId;
                events = db.GetAllNewEventsStream(last, top, MAX_PDUS_PER_TRANSACTION);
            }

            if (events.Count == 0)
            {
                log.Debug("No new events to handle");
                return;
            }

            if (events.Count == MAX_PDUS_PER_TRANSACTION)
            {
                log.Warning("More than {Max} events behind", MAX_PDUS_PER_TRANSACTION);
                top = events.Last().StreamOrdering;
            }

            log.Information("Processing from {last} to {top}", last, top);

            // Skip any events that didn't come from us.
            foreach (var item in events.Where(e => IsMineId(e.Sender)).GroupBy(e => e.RoomId))
            {
                var hosts = await GetHostsInRoom(item.Key);

                foreach (var roomEvent in item)
                {
                    // TODO: Support send_on_bahalf_of?

                    IPduEvent pduEv;

                    // NOTE: This is an event format version, not room version.
                    if (roomEvent.Version == 1)
                        pduEv = new PduEventV1
                        {
                            event_id = roomEvent.EventId
                        };
                    else // Default to latest event format version.
                        pduEv = new PduEventV2();

                    pduEv.content = roomEvent.Content["content"] as JObject;
                    pduEv.origin = _serverName;
                    pduEv.depth = (long) roomEvent.Content["depth"];
                    pduEv.auth_events = roomEvent.Content["auth_events"];
                    pduEv.prev_events = roomEvent.Content["prev_events"];
                    pduEv.origin_server_ts = (long) roomEvent.Content["origin_server_ts"];

                    if (roomEvent.Content.ContainsKey("redacts")) pduEv.redacts = (string) roomEvent.Content["redacts"];

                    pduEv.room_id = roomEvent.RoomId;
                    pduEv.sender = roomEvent.Sender;
                    pduEv.prev_state = roomEvent.Content["prev_state"];

                    if (roomEvent.Content.ContainsKey("state_key"))
                        pduEv.state_key = (string) roomEvent.Content["state_key"];

                    pduEv.type = (string) roomEvent.Content["type"];
                    pduEv.unsigned = (JObject) roomEvent.Content["unsigned"];
                    pduEv.hashes = roomEvent.Content["hashes"];

                    pduEv.signatures = new Dictionary<string, Dictionary<string, string>>();

                    foreach (var sigHosts in (JObject) roomEvent.Content["signatures"])
                    {
                        pduEv.signatures.Add(sigHosts.Key, new Dictionary<string, string>());

                        foreach (var sigs in (JObject) sigHosts.Value)
                            pduEv.signatures[sigHosts.Key].Add(sigs.Key, sigs.Value.Value<string>());
                    }

                    //TODO: I guess we need to fetch the destinations for each event in a room, because someone may have got banned in between.
                    foreach (var host in hosts)
                    {
                        var transaction = GetOrCreateTransactionForDest(host);

                        transaction.pdus.Add(pduEv);
                        // We are handling this elsewhere.
#pragma warning disable 4014
                        AttemptTransaction(host);
#pragma warning restore 4014
                    }
                }
            }

            using (var db = new SynapseDbContext(_connString))
            {
                log.Debug("Saving position {top} to DB", top);
                var streamPos = db.FederationStreamPosition.First(e => e.Type == "events");
                streamPos.StreamId = top;
                db.SaveChanges();
            }

            // Still behind?
            if (events.Count == MAX_PDUS_PER_TRANSACTION)
            {
                log.Information("Calling ProcessPendingEvents again because we are still behind");
                await ProcessPendingEvents();
            }
        }

        private void AttemptTransaction(string destination)
        {
            // Lock here to avoid racing.
            lock (this)
            {
                if (_destOngoingTrans.ContainsKey(destination))
                {
                    if (!_destOngoingTrans[destination].IsCompleted) return;
                    _destOngoingTrans.Remove(destination);
                }

                var t = AttemptNewTransaction(destination);
                _destOngoingTrans.Add(destination, t);
            }
        }

        private async Task AttemptNewTransaction(string destination)
        {
            Transaction currentTransaction;

            if (!TryPopTransaction(destination, out currentTransaction))
            {
                log.Debug("No transactions for {destination}", destination);
                return;
            }
            
            await _concurrentTransactionLock.WaitAsync();

            while (true)
            {
                using (WorkerMetrics.TransactionDurationTimer())
                {
                    try
                    {
                        WorkerMetrics.IncOngoingTransactions();
                        await _client.SendTransaction(currentTransaction);
                        ClearDeviceMessages(currentTransaction);
                        WorkerMetrics.IncTransactionsSent("success", destination);
                    }
                    catch (Exception ex)
                    {
                        log.Warning("Transaction {txnId} {destination} failed: {message}",
                                    currentTransaction.transaction_id, destination, ex.Message);

                        var ts = _backoff.GetBackoffForException(destination, ex);

                        // Some transactions cannot be retried.
                        if (ts != TimeSpan.Zero)
                        {
                            WorkerMetrics.IncTransactionsSent("retry", destination);
                            WorkerMetrics.DecOngoingTransactions();

                            log.Information("Retrying txn {txnId} in {secs}s",
                                            currentTransaction.transaction_id, ts.TotalSeconds);
                            
                            // Release the lock here, as we are going to retry the request again much later.
                            _concurrentTransactionLock.Release();
                            await Task.Delay((int) ts.TotalMilliseconds);
                            await _concurrentTransactionLock.WaitAsync();
                            continue;
                        }

                        WorkerMetrics.IncTransactionsSent("fail", destination);

                        Log.Warning("NOT retrying {txnId} for {destination}", currentTransaction.transaction_id, destination);
                    }
                }

                if (_backoff.ClearBackoff(destination))
                    log.Information("{destination} has come back online", destination);
                
                WorkerMetrics.DecOngoingTransactions();
                WorkerMetrics.IncTransactionEventsSent("pdu", destination, currentTransaction.pdus.Count);
                WorkerMetrics.IncTransactionEventsSent("edu", destination, currentTransaction.edus.Count);

                if (!TryPopTransaction(destination, out currentTransaction))
                {
                    break;
                }
            }

            log.Debug("No more transactions for {destination}", destination);
            _concurrentTransactionLock.Release();
        }

        private void ClearDeviceMessages(Transaction transaction)
        {
            var deviceMsgs = transaction.edus.Where(m => m.edu_type == "m.direct_to_device").ToList()
                                        .ConvertAll(m => m.StreamId);

            var deviceLists = transaction.edus.Where(m => m.edu_type == "m.device_list_update").ToList()
                                         .ConvertAll(m => Tuple.Create(m.StreamId, (string) m.content["user_id"]));

            using (var db = new SynapseDbContext(_connString))
            {
                if (deviceMsgs.Count != 0)
                {
                    _destLastDeviceMsgStreamId[transaction.destination] = deviceMsgs.Max();
                    var deviceMsgEntries = db.DeviceFederationOutboxes.Where(m => deviceMsgs.Contains(m.StreamId));

                    if (deviceMsgEntries.Any())
                    {
                        db.DeviceFederationOutboxes.RemoveRange(deviceMsgEntries);
                        db.SaveChanges();
                    }
                    else
                    {
                        log.Warning("No messages to delete in outbox, despite sending messages in this txn");
                    }
                }

                if (deviceLists.Count == 0) return;

                _destLastDeviceListStreamId[transaction.destination] = deviceLists.Max(e => e.Item1);

                var deviceListEntries = db.DeviceListsOutboundPokes
                                          .Where(m =>
                                                     deviceLists.FindIndex(e => e.Item1 == m.StreamId &&
                                                                                e.Item2 == m.UserId) >= 0);

                if (deviceListEntries.Any())
                {
                    foreach (var msg in deviceListEntries) msg.Sent = true;

                    db.SaveChanges();
                }
                else
                {
                    log.Warning("No device lists to mark as sent, despite sending lists in this txn");
                }
            }
        }

        private bool IsMineId(string id)
        {
            return id.Split(":")[1] == _serverName;
        }

        private JObject FormatPresenceContent(PresenceState state)
        {
            var now = (DateTime.Now - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            var obj = new JObject();
            obj.Add("presence", state.state);
            obj.Add("user_id", state.user_id);

            if (state.last_active_ts != 0) obj.Add("last_active_ago", (int) Math.Round(now - state.last_active_ts));

            if (state.status_msg != null && state.state != "offline") obj.Add("status_msg", state.status_msg);

            if (state.state == "online") obj.Add("currently_active", state.currently_active);

            return obj;
        }

        /// <summary>
        ///     Get a set of remote hosts interested in this presence.
        /// </summary>
        private async Task<Dictionary<string[], PresenceState>> GetInterestedRemotes(List<PresenceState> presenceSet)
        {
            var dict = new Dictionary<string[], PresenceState>();

            using (var db = new SynapseDbContext(_connString))
            {
                // Get the list of rooms shared by these users.
                // We are intentionally skipping presence lists here.
                foreach (var presence in presenceSet)
                {
                    var membershipList = await db
                                              .RoomMemberships
                                              .Where(m =>
                                                         m.Membership == "join" &&
                                                         m.UserId == presence.user_id).ToListAsync();

                    var hosts = new HashSet<string>();

                    // XXX: This is NOT the way to do this, but functions well enough
                    // for a demo.
                    foreach (var roomId in membershipList.ConvertAll(m => m.RoomId))
                        await db.RoomMemberships
                                .Where(m => m.RoomId == roomId)
                                .ForEachAsync(m =>
                                                  hosts.Add(m.UserId
                                                             .Split(":")
                                                              [1]));

                    // Never include ourselves
                    hosts.Remove(_serverName);
                    // Now get the hosts for that room.
                    dict.Add(hosts.ToArray(), presence);
                }
            }

            return dict;
        }

        private async Task<HashSet<string>> GetHostsInRoom(string roomId)
        {
            var hosts = new HashSet<string>();

            using (var db = new SynapseDbContext(_connString))
            {
                await db.RoomMemberships.Where(m => m.RoomId == roomId)
                        .ForEachAsync(m =>
                                          hosts.Add(m.UserId.Split(":")[1]));
            }

            hosts.Remove(_serverName);
            return hosts;
        }

        private long GetTs()
        {
            return (long) (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
        }

        private Transaction GetOrCreateTransactionForDest(string dest)
        {
            LinkedList<Transaction> list;

            if (!_destPendingTransactions.ContainsKey(dest))
            {
                list = new LinkedList<Transaction>();

                if (!_destPendingTransactions.TryAdd(dest, list))
                {
                    list = _destPendingTransactions[dest];
                };
            }
            else
            {
                list = _destPendingTransactions[dest];
            }

            if (list.Count > 0)
            {
                var value = list.Last.Value;

                // If there is still room in the transaction.
                if (value.pdus.Count < MAX_PDUS_PER_TRANSACTION && value.edus.Count < MAX_EDUS_PER_TRANSACTION)
                {
                    return value;
                }

                log.Debug("{host} has gone over a PDU/EDU transaction limit, creating a new transaction", dest);
            }
            
            _txnId++;

            var transaction = new Transaction
            {
                edus = new List<EduEvent>(),
                pdus = new List<IPduEvent>(),
                origin = _serverName,
                origin_server_ts = GetTs(),
                transaction_id = _txnId.ToString(),
                destination = dest
            };

            list.AddLast(transaction);

            return transaction;
        }

        private bool TryPopTransaction(string dest, out Transaction t)
        {
            if (_destPendingTransactions.ContainsKey(dest) && _destPendingTransactions[dest].Count > 0)
            {
                t = _destPendingTransactions[dest].First();
                _destPendingTransactions[dest].RemoveFirst();
                return true;
            }

            t = default(Transaction);
            return false;
        }
    }
}
