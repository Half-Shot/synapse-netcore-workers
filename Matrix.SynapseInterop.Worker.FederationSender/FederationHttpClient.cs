using System;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using Matrix.SynapseInterop.Common;

namespace Matrix.SynapseInterop.Worker.FederationSender
{
    public class FederationHttpClient : HttpClient
    {
        public FederationHttpClient(bool allowSelfSigned) : base(new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (sender, certificate, chain, sslpolicyerrors) => 
                    CheckCert(sslpolicyerrors, allowSelfSigned)
            },
            UseProxy = false,
            UseCookies = false,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromSeconds(15)
        })
        {
            Timeout = TimeSpan.FromMinutes(1);
        }
        
        private static bool CheckCert(SslPolicyErrors sslpolicyerrors,
                                      bool allowSelfSigned
        )
        {
            if (sslpolicyerrors.HasFlag(SslPolicyErrors.None)) return true;
        
            return sslpolicyerrors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) &&
                   sslpolicyerrors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable) &&
                   allowSelfSigned;
        }

        public override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            WorkerMetrics.IncOngoingHttpConnections();

            try
            {
                var t = base.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                return t;
            }
            finally
            {
                WorkerMetrics.DecOngoingHttpConnections();
            }
        }
    }
}
