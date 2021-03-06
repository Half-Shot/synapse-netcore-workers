﻿using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using Matrix.SynapseInterop.Replication.DataRows;

namespace Matrix.SynapseInterop.Replication
{
    public class SynapseReplication
    {
        private TcpClient _client;
        private Timer _pingTimer;
        private Dictionary<string, ReplicationData> _pendingBatches = new Dictionary<string, ReplicationData>();
        private Dictionary<Type, object> _streams = new Dictionary<Type, object>(); // object is a ReplicationStream<T>. TODO: Don't do this.

        public string ClientName { get; set; }

        public event EventHandler<string> ServerName;
        public event EventHandler<ReplicationData> RData;
        public event EventHandler<StreamPosition> PositionUpdate;
        public event EventHandler<string> Error;
        public event EventHandler<string> Ping;

        public async Task Connect(string address, int port)
        {
            Disconnect();

            // Resolve the address
            var dns = await Dns.GetHostEntryAsync(address);
            var ip = dns.AddressList[0];

            // Form a connection
            _client = new TcpClient();
            await _client.ConnectAsync(ip, port);

            // Name our client
            string name = string.IsNullOrWhiteSpace(ClientName) ? "NETCORESynapseReplication" : ClientName;
            SendRaw("NAME " + name);

            // Start pinging 
            _pingTimer = new Timer(SendPing, null, TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(5));

            // Start the reader
#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
            Task.Run(() => ReadLoop());
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
        }

        public void Disconnect()
        {
            if (_client != null) _client.Close();

            // Stop the timer - we'll create a new one if we need to
            if (_pingTimer != null) _pingTimer.Change(Timeout.Infinite, Timeout.Infinite);

            _pendingBatches.Clear();
        }

        private void ReadLoop()
        {
            while (_client.Connected)
            {
                byte[] buf = new byte[1024];
                var stream = _client.GetStream();

                var result = new StringBuilder();
                int read = 0;

                do
                {
                    read = stream.Read(buf, 0, buf.Length);
                    result.Append(Encoding.UTF8.GetString(buf, 0, read));
                } while (stream.DataAvailable);

                ProcessCommands(result.ToString());
            }
        }

        private void ProcessCommands(string raw)
        {
            var byLine = raw.Split('\n').Where(c => !string.IsNullOrWhiteSpace(c));

            foreach (string cmd in byLine)
            {
                if (cmd.StartsWith("SERVER "))
                {
                    if (ServerName == null) continue;
                    ServerName(this, cmd.Substring("SERVER ".Length));
                }
                else if (cmd.StartsWith("RDATA "))
                {
                    if (RData == null) continue;
                    var row = cmd.Substring("RDATA ".Length);
                    string[] rowParts = row.Split(new char[] { ' ' }, 3);

                    string stream = rowParts[0];
                    string position = rowParts[1];
                    string rowData = rowParts[2];

                    if (!_pendingBatches.ContainsKey(stream)) _pendingBatches.Add(stream, new ReplicationData(stream));
                    _pendingBatches[stream].AppendRow(rowData);
                    if (position != "batch")
                    {
                        var rdata = _pendingBatches[stream];
                        rdata.Position = position;
                        _pendingBatches.Remove(stream);
                        RData(this, rdata);
                    }
                }
                else if (cmd.StartsWith("POSITION "))
                {
                    if (PositionUpdate == null) continue;
                    var posParts = cmd.Substring("POSITION ".Length).Split(new char[] { ' ' }, 2);

                    string stream = posParts[0];
                    string position = posParts[1];

                    PositionUpdate(this, new StreamPosition { StreamName = stream, Position = position });
                }
                else if (cmd.StartsWith("PING "))
                {
                    if (Ping == null) continue;
                    Ping(this, cmd.Substring("PING ".Length));
                }
                else if (cmd.StartsWith("ERROR "))
                {
                    if (Error == null) continue;
                    Error(this, cmd.Substring("ERROR ".Length));
                }
            }
        }

        public void SendRaw(string command)
        {
            _client.Client.Send(Encoding.UTF8.GetBytes(command + "\n"));
        }

        private void SendPing(Object context)
        {
            SendRaw("PING " + DateTime.Now.ToBinary());
        }

        public void SubscribeStream(string streamName, string position)
        {
            SendRaw("REPLICATE " + streamName + " " + position);
        }

        public ReplicationStream<T> BindStream<T>() where T : IReplicationDataRow
        {
            if (!_streams.ContainsKey(typeof(T))) ResumeStream<T>(StreamPosition.LATEST);
            return (ReplicationStream<T>)_streams[typeof(T)];
        }

        public ReplicationStream<T> ResumeStream<T>(string fromPosition) where T : IReplicationDataRow
        {
            if (_streams.ContainsKey(typeof(T))) throw new ArgumentException("A stream has already been started");
            _streams.Add(typeof(T), new ReplicationStream<T>(this, fromPosition));
            return (ReplicationStream<T>)_streams[typeof(T)];
        }
    }
}
