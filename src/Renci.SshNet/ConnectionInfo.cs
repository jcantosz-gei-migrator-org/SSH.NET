﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;

using Renci.SshNet.Common;
using Renci.SshNet.Compression;
using Renci.SshNet.Messages.Authentication;
using Renci.SshNet.Messages.Connection;
using Renci.SshNet.Security;
using Renci.SshNet.Security.Cryptography;
using Renci.SshNet.Security.Cryptography.Ciphers;

using CipherMode = System.Security.Cryptography.CipherMode;

namespace Renci.SshNet
{
    /// <summary>
    /// Represents remote connection information class.
    /// </summary>
    /// <remarks>
    /// This class is NOT thread-safe. Do not use the same <see cref="ConnectionInfo"/> with multiple
    /// client instances.
    /// </remarks>
    public class ConnectionInfo : IConnectionInfoInternal
    {
        internal const int DefaultPort = 22;

        /// <summary>
        /// The default connection timeout.
        /// </summary>
        /// <value>
        /// 30 seconds.
        /// </value>
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

        /// <summary>
        /// The default channel close timeout.
        /// </summary>
        /// <value>
        /// 1 second.
        /// </value>
        private static readonly TimeSpan DefaultChannelCloseTimeout = TimeSpan.FromSeconds(1);

        private TimeSpan _timeout;
        private TimeSpan _channelCloseTimeout;

        /// <summary>
        /// Gets supported key exchange algorithms for this connection.
        /// </summary>
        public IOrderedDictionary<string, Func<IKeyExchange>> KeyExchangeAlgorithms { get; }

        /// <summary>
        /// Gets supported encryptions for this connection.
        /// </summary>
        public IOrderedDictionary<string, CipherInfo> Encryptions { get; }

        /// <summary>
        /// Gets supported hash algorithms for this connection.
        /// </summary>
        public IOrderedDictionary<string, HashInfo> HmacAlgorithms { get; }

        /// <summary>
        /// Gets supported host key algorithms for this connection.
        /// </summary>
        public IOrderedDictionary<string, Func<byte[], KeyHostAlgorithm>> HostKeyAlgorithms { get; }

        /// <summary>
        /// Gets supported authentication methods for this connection.
        /// </summary>
        public IList<AuthenticationMethod> AuthenticationMethods { get; }

        /// <summary>
        /// Gets supported compression algorithms for this connection.
        /// </summary>
        public IOrderedDictionary<string, Func<Compressor>> CompressionAlgorithms { get; }

        /// <summary>
        /// Gets the supported channel requests for this connection.
        /// </summary>
        /// <value>
        /// The supported channel requests for this connection.
        /// </value>
        public IDictionary<string, RequestInfo> ChannelRequests { get; }

        /// <summary>
        /// Gets a value indicating whether connection is authenticated.
        /// </summary>
        /// <value>
        /// <see langword="true"/> if connection is authenticated; otherwise, <see langword="false"/>.
        /// </value>
        public bool IsAuthenticated { get; private set; }

        /// <summary>
        /// Gets connection host.
        /// </summary>
        /// <value>
        /// The connection host.
        /// </value>
        public string Host { get; }

        /// <summary>
        /// Gets connection port.
        /// </summary>
        /// <value>
        /// The connection port. The default value is 22.
        /// </value>
        public int Port { get; }

        /// <summary>
        /// Gets connection username.
        /// </summary>
        public string Username { get; }

        /// <summary>
        /// Gets proxy type.
        /// </summary>
        /// <value>
        /// The type of the proxy.
        /// </value>
        public ProxyTypes ProxyType { get; }

        /// <summary>
        /// Gets proxy connection host.
        /// </summary>
        public string ProxyHost { get; }

        /// <summary>
        /// Gets proxy connection port.
        /// </summary>
        public int ProxyPort { get; }

        /// <summary>
        /// Gets proxy connection username.
        /// </summary>
        public string ProxyUsername { get; }

        /// <summary>
        /// Gets proxy connection password.
        /// </summary>
        public string ProxyPassword { get; }

        /// <summary>
        /// Gets or sets connection timeout.
        /// </summary>
        /// <value>
        /// The connection timeout. The default value is 30 seconds.
        /// </value>
        public TimeSpan Timeout
        {
            get
            {
                return _timeout;
            }
            set
            {
                value.EnsureValidTimeout(nameof(Timeout));

                _timeout = value;
            }
        }

        /// <summary>
        /// Gets or sets the timeout to use when waiting for a server to acknowledge closing a channel.
        /// </summary>
        /// <value>
        /// The channel close timeout. The default value is 1 second.
        /// </value>
        /// <remarks>
        /// If a server does not send a <c>SSH_MSG_CHANNEL_CLOSE</c> message before the specified timeout
        /// elapses, the channel will be closed immediately.
        /// </remarks>
        public TimeSpan ChannelCloseTimeout
        {
            get
            {
                return _channelCloseTimeout;
            }
            set
            {
                value.EnsureValidTimeout(nameof(ChannelCloseTimeout));

                _channelCloseTimeout = value;
            }
        }

        /// <summary>
        /// Gets or sets the character encoding.
        /// </summary>
        /// <value>
        /// The character encoding. The default is <see cref="Encoding.UTF8"/>.
        /// </value>
        public Encoding Encoding { get; set; }

        /// <summary>
        /// Gets or sets number of retry attempts when session channel creation failed.
        /// </summary>
        /// <value>
        /// The number of retry attempts when session channel creation failed. The default
        /// value is 10.
        /// </value>
        public int RetryAttempts { get; set; }

        /// <summary>
        /// Gets or sets maximum number of session channels to be open simultaneously.
        /// </summary>
        /// <value>
        /// The maximum number of session channels to be open simultaneously. The default
        /// value is 10.
        /// </value>
        public int MaxSessions { get; set; }

        /// <summary>
        /// Occurs when authentication banner is sent by the server.
        /// </summary>
        public event EventHandler<AuthenticationBannerEventArgs> AuthenticationBanner;

        /// <summary>
        /// Gets the current key exchange algorithm.
        /// </summary>
        public string CurrentKeyExchangeAlgorithm { get; internal set; }

        /// <summary>
        /// Gets the current server encryption.
        /// </summary>
        public string CurrentServerEncryption { get; internal set; }

        /// <summary>
        /// Gets the current client encryption.
        /// </summary>
        public string CurrentClientEncryption { get; internal set; }

        /// <summary>
        /// Gets the current server hash algorithm.
        /// </summary>
        public string CurrentServerHmacAlgorithm { get; internal set; }

        /// <summary>
        /// Gets the current client hash algorithm.
        /// </summary>
        public string CurrentClientHmacAlgorithm { get; internal set; }

        /// <summary>
        /// Gets the current host key algorithm.
        /// </summary>
        public string CurrentHostKeyAlgorithm { get; internal set; }

        /// <summary>
        /// Gets the current server compression algorithm.
        /// </summary>
        public string CurrentServerCompressionAlgorithm { get; internal set; }

        /// <summary>
        /// Gets the server version.
        /// </summary>
        public string ServerVersion { get; internal set; }

        /// <summary>
        /// Gets the client version.
        /// </summary>
        public string ClientVersion { get; internal set; }

        /// <summary>
        /// Gets the current client compression algorithm.
        /// </summary>
        public string CurrentClientCompressionAlgorithm { get; internal set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="ConnectionInfo"/> class.
        /// </summary>
        /// <param name="host">The host.</param>
        /// <param name="username">The username.</param>
        /// <param name="authenticationMethods">The authentication methods.</param>
        /// <exception cref="ArgumentNullException"><paramref name="host"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="host"/> is a zero-length string.</exception>
        /// <exception cref="ArgumentException"><paramref name="username" /> is <see langword="null"/>, a zero-length string or contains only whitespace characters.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="authenticationMethods"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">No <paramref name="authenticationMethods"/> specified.</exception>
        public ConnectionInfo(string host, string username, params AuthenticationMethod[] authenticationMethods)
            : this(host, DefaultPort, username, ProxyTypes.None, proxyHost: null, 0, proxyUsername: null, proxyPassword: null, authenticationMethods)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ConnectionInfo"/> class.
        /// </summary>
        /// <param name="host">The host.</param>
        /// <param name="port">The port.</param>
        /// <param name="username">The username.</param>
        /// <param name="authenticationMethods">The authentication methods.</param>
        /// <exception cref="ArgumentNullException"><paramref name="host"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="username" /> is <see langword="null"/>, a zero-length string or contains only whitespace characters.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="port" /> is not within <see cref="IPEndPoint.MinPort" /> and <see cref="IPEndPoint.MaxPort" />.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="authenticationMethods"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">No <paramref name="authenticationMethods"/> specified.</exception>
        public ConnectionInfo(string host, int port, string username, params AuthenticationMethod[] authenticationMethods)
            : this(host, port, username, ProxyTypes.None, proxyHost: null, 0, proxyUsername: null, proxyPassword: null, authenticationMethods)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ConnectionInfo" /> class.
        /// </summary>
        /// <param name="host">Connection host.</param>
        /// <param name="port">Connection port.</param>
        /// <param name="username">Connection username.</param>
        /// <param name="proxyType">Type of the proxy.</param>
        /// <param name="proxyHost">The proxy host.</param>
        /// <param name="proxyPort">The proxy port.</param>
        /// <param name="proxyUsername">The proxy username.</param>
        /// <param name="proxyPassword">The proxy password.</param>
        /// <param name="authenticationMethods">The authentication methods.</param>
        /// <exception cref="ArgumentNullException"><paramref name="host"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="username" /> is <see langword="null"/>, a zero-length string or contains only whitespace characters.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="port" /> is not within <see cref="IPEndPoint.MinPort" /> and <see cref="IPEndPoint.MaxPort" />.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="proxyType"/> is not <see cref="ProxyTypes.None"/> and <paramref name="proxyHost" /> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="proxyType"/> is not <see cref="ProxyTypes.None"/> and <paramref name="proxyPort" /> is not within <see cref="IPEndPoint.MinPort" /> and <see cref="IPEndPoint.MaxPort" />.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="authenticationMethods"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">No <paramref name="authenticationMethods"/> specified.</exception>
        public ConnectionInfo(string host, int port, string username, ProxyTypes proxyType, string proxyHost, int proxyPort, string proxyUsername, string proxyPassword, params AuthenticationMethod[] authenticationMethods)
        {
            ThrowHelper.ThrowIfNull(host);
            port.ValidatePort();
            ThrowHelper.ThrowIfNullOrWhiteSpace(username);

            if (proxyType != ProxyTypes.None)
            {
                ThrowHelper.ThrowIfNull(proxyHost);
                proxyPort.ValidatePort();
            }

            ThrowHelper.ThrowIfNull(authenticationMethods);

            if (authenticationMethods.Length == 0)
            {
                throw new ArgumentException("At least one authentication method should be specified.", nameof(authenticationMethods));
            }

            // Set default connection values
            Timeout = DefaultTimeout;
            ChannelCloseTimeout = DefaultChannelCloseTimeout;
            RetryAttempts = 10;
            MaxSessions = 10;
            Encoding = Encoding.UTF8;

            KeyExchangeAlgorithms = new OrderedDictionary<string, Func<IKeyExchange>>
                {
                    { "mlkem768x25519-sha256", () => new KeyExchangeMLKem768X25519Sha256() },
                    { "sntrup761x25519-sha512", () => new KeyExchangeSNtruP761X25519Sha512() },
                    { "sntrup761x25519-sha512@openssh.com", () => new KeyExchangeSNtruP761X25519Sha512() },
                    { "curve25519-sha256", () => new KeyExchangeECCurve25519() },
                    { "curve25519-sha256@libssh.org", () => new KeyExchangeECCurve25519() },
                    { "ecdh-sha2-nistp256", () => new KeyExchangeECDH256() },
                    { "ecdh-sha2-nistp384", () => new KeyExchangeECDH384() },
                    { "ecdh-sha2-nistp521", () => new KeyExchangeECDH521() },
                    { "diffie-hellman-group-exchange-sha256", () => new KeyExchangeDiffieHellmanGroupExchangeSha256() },
                    { "diffie-hellman-group-exchange-sha1", () => new KeyExchangeDiffieHellmanGroupExchangeSha1() },
                    { "diffie-hellman-group16-sha512", () => new KeyExchangeDiffieHellmanGroup16Sha512() },
                    { "diffie-hellman-group14-sha256", () => new KeyExchangeDiffieHellmanGroup14Sha256() },
                    { "diffie-hellman-group14-sha1", () => new KeyExchangeDiffieHellmanGroup14Sha1() },
                    { "diffie-hellman-group1-sha1", () => new KeyExchangeDiffieHellmanGroup1Sha1() },
                };

            Encryptions = new OrderedDictionary<string, CipherInfo>
                {
                    { "aes128-ctr", new CipherInfo(128, (key, iv) => new AesCipher(key, iv, AesCipherMode.CTR, pkcs7Padding: false)) },
                    { "aes192-ctr", new CipherInfo(192, (key, iv) => new AesCipher(key, iv, AesCipherMode.CTR, pkcs7Padding: false)) },
                    { "aes256-ctr", new CipherInfo(256, (key, iv) => new AesCipher(key, iv, AesCipherMode.CTR, pkcs7Padding: false)) },
                    { "aes128-gcm@openssh.com", new CipherInfo(128, (key, iv) => new AesGcmCipher(key, iv, aadLength: 4), isAead: true) },
                    { "aes256-gcm@openssh.com", new CipherInfo(256, (key, iv) => new AesGcmCipher(key, iv, aadLength: 4), isAead: true) },
                    { "chacha20-poly1305@openssh.com", new CipherInfo(512, (key, iv) => new ChaCha20Poly1305Cipher(key, aadLength: 4), isAead: true) },
                    { "aes128-cbc", new CipherInfo(128, (key, iv) => new AesCipher(key, iv, AesCipherMode.CBC, pkcs7Padding: false)) },
                    { "aes192-cbc", new CipherInfo(192, (key, iv) => new AesCipher(key, iv, AesCipherMode.CBC, pkcs7Padding: false)) },
                    { "aes256-cbc", new CipherInfo(256, (key, iv) => new AesCipher(key, iv, AesCipherMode.CBC, pkcs7Padding: false)) },
                    { "3des-cbc", new CipherInfo(192, (key, iv) => new TripleDesCipher(key, iv, CipherMode.CBC, pkcs7Padding: false)) },
                };

            HmacAlgorithms = new OrderedDictionary<string, HashInfo>
                {
                    /* Encrypt-and-MAC (encrypt-and-authenticate) variants */
                    { "hmac-sha2-256", new HashInfo(32*8, key => new HMACSHA256(key)) },
                    { "hmac-sha2-512", new HashInfo(64*8, key => new HMACSHA512(key)) },
                    { "hmac-sha1", new HashInfo(20*8, key => new HMACSHA1(key)) },
                    /* Encrypt-then-MAC variants */
                    { "hmac-sha2-256-etm@openssh.com", new HashInfo(32*8, key => new HMACSHA256(key), isEncryptThenMAC: true) },
                    { "hmac-sha2-512-etm@openssh.com", new HashInfo(64*8, key => new HMACSHA512(key), isEncryptThenMAC: true) },
                    { "hmac-sha1-etm@openssh.com", new HashInfo(20*8, key => new HMACSHA1(key), isEncryptThenMAC: true) },
                };

#pragma warning disable SA1107 // Code should not contain multiple statements on one line
            var hostAlgs = new OrderedDictionary<string, Func<byte[], KeyHostAlgorithm>>();
            hostAlgs.Add("ssh-ed25519-cert-v01@openssh.com", data => { var cert = new Certificate(data); return new CertificateHostAlgorithm("ssh-ed25519-cert-v01@openssh.com", cert, hostAlgs); });
            hostAlgs.Add("ecdsa-sha2-nistp256-cert-v01@openssh.com", data => { var cert = new Certificate(data); return new CertificateHostAlgorithm("ecdsa-sha2-nistp256-cert-v01@openssh.com", cert, hostAlgs); });
            hostAlgs.Add("ecdsa-sha2-nistp384-cert-v01@openssh.com", data => { var cert = new Certificate(data); return new CertificateHostAlgorithm("ecdsa-sha2-nistp384-cert-v01@openssh.com", cert, hostAlgs); });
            hostAlgs.Add("ecdsa-sha2-nistp521-cert-v01@openssh.com", data => { var cert = new Certificate(data); return new CertificateHostAlgorithm("ecdsa-sha2-nistp521-cert-v01@openssh.com", cert, hostAlgs); });
            hostAlgs.Add("rsa-sha2-512-cert-v01@openssh.com", data => { var cert = new Certificate(data); return new CertificateHostAlgorithm("rsa-sha2-512-cert-v01@openssh.com", cert, new RsaDigitalSignature((RsaKey)cert.Key, HashAlgorithmName.SHA512), hostAlgs); });
            hostAlgs.Add("rsa-sha2-256-cert-v01@openssh.com", data => { var cert = new Certificate(data); return new CertificateHostAlgorithm("rsa-sha2-256-cert-v01@openssh.com", cert, new RsaDigitalSignature((RsaKey)cert.Key, HashAlgorithmName.SHA256), hostAlgs); });
            hostAlgs.Add("ssh-rsa-cert-v01@openssh.com", data => { var cert = new Certificate(data); return new CertificateHostAlgorithm("ssh-rsa-cert-v01@openssh.com", cert, hostAlgs); });
            hostAlgs.Add("ssh-ed25519", data => new KeyHostAlgorithm("ssh-ed25519", new ED25519Key(new SshKeyData(data))));
            hostAlgs.Add("ecdsa-sha2-nistp256", data => new KeyHostAlgorithm("ecdsa-sha2-nistp256", new EcdsaKey(new SshKeyData(data))));
            hostAlgs.Add("ecdsa-sha2-nistp384", data => new KeyHostAlgorithm("ecdsa-sha2-nistp384", new EcdsaKey(new SshKeyData(data))));
            hostAlgs.Add("ecdsa-sha2-nistp521", data => new KeyHostAlgorithm("ecdsa-sha2-nistp521", new EcdsaKey(new SshKeyData(data))));
            hostAlgs.Add("rsa-sha2-512", data => { var key = new RsaKey(new SshKeyData(data)); return new KeyHostAlgorithm("rsa-sha2-512", key, new RsaDigitalSignature(key, HashAlgorithmName.SHA512)); });
            hostAlgs.Add("rsa-sha2-256", data => { var key = new RsaKey(new SshKeyData(data)); return new KeyHostAlgorithm("rsa-sha2-256", key, new RsaDigitalSignature(key, HashAlgorithmName.SHA256)); });
            hostAlgs.Add("ssh-rsa", data => new KeyHostAlgorithm("ssh-rsa", new RsaKey(new SshKeyData(data))));
#pragma warning restore SA1107 // Code should not contain multiple statements on one line
            HostKeyAlgorithms = hostAlgs;

            CompressionAlgorithms = new OrderedDictionary<string, Func<Compressor>>
                {
                    { "none", null },
                    { "zlib@openssh.com", () => new ZlibOpenSsh() },
                };

            ChannelRequests = new Dictionary<string, RequestInfo>
                {
                    { EnvironmentVariableRequestInfo.Name, new EnvironmentVariableRequestInfo() },
                    { ExecRequestInfo.Name, new ExecRequestInfo() },
                    { ExitSignalRequestInfo.Name, new ExitSignalRequestInfo() },
                    { ExitStatusRequestInfo.Name, new ExitStatusRequestInfo() },
                    { PseudoTerminalRequestInfo.Name, new PseudoTerminalRequestInfo() },
                    { ShellRequestInfo.Name, new ShellRequestInfo() },
                    { SignalRequestInfo.Name, new SignalRequestInfo() },
                    { SubsystemRequestInfo.Name, new SubsystemRequestInfo() },
                    { WindowChangeRequestInfo.Name, new WindowChangeRequestInfo() },
                    { X11ForwardingRequestInfo.Name, new X11ForwardingRequestInfo() },
                    { XonXoffRequestInfo.Name, new XonXoffRequestInfo() },
                    { EndOfWriteRequestInfo.Name, new EndOfWriteRequestInfo() },
                    { KeepAliveRequestInfo.Name, new KeepAliveRequestInfo() },
                };

            Host = host;
            Port = port;
            Username = username;

            ProxyType = proxyType;
            ProxyHost = proxyHost;
            ProxyPort = proxyPort;
            ProxyUsername = proxyUsername;
            ProxyPassword = proxyPassword;

            AuthenticationMethods = authenticationMethods;
        }

        /// <summary>
        /// Authenticates the specified session.
        /// </summary>
        /// <param name="session">The session to be authenticated.</param>
        /// <param name="serviceFactory">The factory to use for creating new services.</param>
        /// <exception cref="ArgumentNullException"><paramref name="session"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="serviceFactory"/> is <see langword="null"/>.</exception>
        /// <exception cref="SshAuthenticationException">No suitable authentication method found to complete authentication, or permission denied.</exception>
        internal void Authenticate(ISession session, IServiceFactory serviceFactory)
        {
            ThrowHelper.ThrowIfNull(serviceFactory);

            IsAuthenticated = false;
            var clientAuthentication = serviceFactory.CreateClientAuthentication();
            clientAuthentication.Authenticate(this, session);
            IsAuthenticated = true;
        }

        /// <summary>
        /// Signals that an authentication banner message was received from the server.
        /// </summary>
        /// <param name="sender">The session in which the banner message was received.</param>
        /// <param name="e">The banner message.</param>
        void IConnectionInfoInternal.UserAuthenticationBannerReceived(object sender, MessageEventArgs<BannerMessage> e)
        {
            AuthenticationBanner?.Invoke(this, new AuthenticationBannerEventArgs(Username, e.Message.Message, e.Message.Language));
        }

        /// <summary>
        /// Creates a <c>none</c> authentication method.
        /// </summary>
        /// <returns>
        /// A <c>none</c> authentication method.
        /// </returns>
        IAuthenticationMethod IConnectionInfoInternal.CreateNoneAuthenticationMethod()
        {
            return new NoneAuthenticationMethod(Username);
        }

        /// <summary>
        /// Gets the supported authentication methods for this connection.
        /// </summary>
        /// <value>
        /// The supported authentication methods for this connection.
        /// </value>
        IList<IAuthenticationMethod> IConnectionInfoInternal.AuthenticationMethods
        {
#pragma warning disable S2365 // Properties should not make collection or array copies
            get { return AuthenticationMethods.Cast<IAuthenticationMethod>().ToList(); }
#pragma warning restore S2365 // Properties should not make collection or array copies
        }
    }
}
