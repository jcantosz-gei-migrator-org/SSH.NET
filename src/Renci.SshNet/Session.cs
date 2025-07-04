﻿using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
#if !NET
using System.Text;
#endif
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

using Renci.SshNet.Abstractions;
using Renci.SshNet.Channels;
using Renci.SshNet.Common;
using Renci.SshNet.Compression;
using Renci.SshNet.Connection;
using Renci.SshNet.Messages;
using Renci.SshNet.Messages.Authentication;
using Renci.SshNet.Messages.Connection;
using Renci.SshNet.Messages.Transport;
using Renci.SshNet.Security;
using Renci.SshNet.Security.Cryptography;

namespace Renci.SshNet
{
    /// <summary>
    /// Provides functionality to connect and interact with SSH server.
    /// </summary>
    public class Session : ISession
    {
        internal const byte CarriageReturn = 0x0d;
        internal const byte LineFeed = 0x0a;

        private static readonly string ClientVersionString =
            "SSH-2.0-Renci.SshNet.SshClient." + ThisAssembly.NuGetPackageVersion.Replace('-', '_');

        /// <summary>
        /// Specifies maximum packet size defined by the protocol.
        /// </summary>
        /// <value>
        /// 68536 (64 KB + 3000 bytes).
        /// </value>
        internal const int MaximumSshPacketSize = LocalChannelDataPacketSize + 3000;

        /// <summary>
        /// Holds the initial local window size for the channels.
        /// </summary>
        /// <value>
        /// 2147483647 (2^31 - 1) bytes.
        /// </value>
        /// <remarks>
        /// We currently do not define a maximum (remote) window size.
        /// </remarks>
        private const int InitialLocalWindowSize = 0x7FFFFFFF;

        /// <summary>
        /// Holds the maximum size of channel data packets that we receive.
        /// </summary>
        /// <value>
        /// 64 KB.
        /// </value>
        /// <remarks>
        /// <para>
        /// This is the maximum size (in bytes) we support for the data (payload) of a
        /// <c>SSH_MSG_CHANNEL_DATA</c> message we receive.
        /// </para>
        /// <para>
        /// We currently do not enforce this limit.
        /// </para>
        /// </remarks>
        private const int LocalChannelDataPacketSize = 1024 * 64;

        /// <summary>
        /// Holds the factory to use for creating new services.
        /// </summary>
        private readonly IServiceFactory _serviceFactory;
        private readonly ISocketFactory _socketFactory;
        private readonly ILogger _logger;

        /// <summary>
        /// Holds an object that is used to ensure only a single thread can read from
        /// <see cref="_socket"/> at any given time.
        /// </summary>
        private readonly Lock _socketReadLock = new Lock();

        /// <summary>
        /// Holds an object that is used to ensure only a single thread can write to
        /// <see cref="_socket"/> at any given time.
        /// </summary>
        /// <remarks>
        /// This is also used to ensure that <see cref="_outboundPacketSequence"/> is
        /// incremented atomatically.
        /// </remarks>
        private readonly Lock _socketWriteLock = new Lock();

        /// <summary>
        /// Holds an object that is used to ensure only a single thread can dispose
        /// <see cref="_socket"/> at any given time.
        /// </summary>
        /// <remarks>
        /// This is also used to ensure that <see cref="_socket"/> will not be disposed
        /// while performing a given operation or set of operations on <see cref="_socket"/>.
        /// </remarks>
        private readonly SemaphoreSlim _socketDisposeLock = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Holds an object that is used to ensure only a single thread can connect
        /// at any given time.
        /// </summary>
        private readonly SemaphoreSlim _connectLock = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Holds metadata about session messages.
        /// </summary>
        private SshMessageFactory _sshMessageFactory;

        /// <summary>
        /// Holds a <see cref="WaitHandle"/> that is signaled when the message listener loop has completed.
        /// </summary>
        private ManualResetEvent _messageListenerCompleted;

        /// <summary>
        /// Specifies outbound packet number.
        /// </summary>
        private volatile uint _outboundPacketSequence;

        /// <summary>
        /// Specifies incoming packet number.
        /// </summary>
        private uint _inboundPacketSequence;

        /// <summary>
        /// WaitHandle to signal that last service request was accepted.
        /// </summary>
        private EventWaitHandle _serviceAccepted = new AutoResetEvent(initialState: false);

        /// <summary>
        /// WaitHandle to signal that exception was thrown by another thread.
        /// </summary>
        private EventWaitHandle _exceptionWaitHandle = new ManualResetEvent(initialState: false);

        /// <summary>
        /// WaitHandle to signal that key exchange was completed.
        /// </summary>
        private ManualResetEventSlim _keyExchangeCompletedWaitHandle = new ManualResetEventSlim(initialState: false);

        /// <summary>
        /// Exception that need to be thrown by waiting thread.
        /// </summary>
        private Exception _exception;

        /// <summary>
        /// Specifies whether connection is authenticated.
        /// </summary>
        private bool _isAuthenticated;

        /// <summary>
        /// Specifies whether user issued Disconnect command or not.
        /// </summary>
        private bool _isDisconnecting;

        /// <summary>
        /// Indicates whether it is the init kex.
        /// </summary>
        private bool _isInitialKex;

        /// <summary>
        /// Indicates whether server supports strict key exchange.
        /// <see href="https://github.com/openssh/openssh-portable/blob/master/PROTOCOL"/> 1.10.
        /// </summary>
        private bool _isStrictKex;

        private IKeyExchange _keyExchange;

        private HashAlgorithm _serverMac;

        private HashAlgorithm _clientMac;

        private bool _serverEtm;

        private bool _clientEtm;

        private Cipher _serverCipher;

        private Cipher _clientCipher;

        private bool _serverAead;

        private bool _clientAead;

        private Compressor _serverDecompression;

        private Compressor _clientCompression;

        private SemaphoreSlim _sessionSemaphore;

        private bool _isDisconnectMessageSent;

        private int _nextChannelNumber;

        /// <summary>
        /// Holds connection socket.
        /// </summary>
        private Socket _socket;

        /// <summary>
        /// Gets the session semaphore that controls session channels.
        /// </summary>
        /// <value>
        /// The session semaphore.
        /// </value>
        public SemaphoreSlim SessionSemaphore
        {
            get
            {
                if (_sessionSemaphore is SemaphoreSlim sessionSemaphore)
                {
                    return sessionSemaphore;
                }

                sessionSemaphore = new SemaphoreSlim(ConnectionInfo.MaxSessions);

                if (Interlocked.CompareExchange(ref _sessionSemaphore, sessionSemaphore, comparand: null) is not null)
                {
                    // Another thread has set _sessionSemaphore. Dispose our one.
                    Debug.Assert(_sessionSemaphore != sessionSemaphore);
                    sessionSemaphore.Dispose();
                }

                return _sessionSemaphore;
            }
        }

        /// <summary>
        /// Gets the next channel number.
        /// </summary>
        /// <value>
        /// The next channel number.
        /// </value>
        private uint NextChannelNumber
        {
            get
            {
                return (uint)Interlocked.Increment(ref _nextChannelNumber);
            }
        }

        /// <summary>
        /// Gets a value indicating whether the session is connected.
        /// </summary>
        /// <value>
        /// <see langword="true"/> if the session is connected; otherwise, <see langword="false"/>.
        /// </value>
        /// <remarks>
        /// This methods returns <see langword="true"/> in all but the following cases:
        /// <list type="bullet">
        ///     <item>
        ///         <description>The <see cref="Session"/> is disposed.</description>
        ///     </item>
        ///     <item>
        ///         <description>The <c>SSH_MSG_DISCONNECT</c> message - which is used to disconnect from the server - has been sent.</description>
        ///     </item>
        ///     <item>
        ///         <description>The client has not been authenticated successfully.</description>
        ///     </item>
        ///     <item>
        ///         <description>The listener thread - which is used to receive messages from the server - has stopped.</description>
        ///     </item>
        ///     <item>
        ///         <description>The socket used to communicate with the server is no longer connected.</description>
        ///     </item>
        /// </list>
        /// </remarks>
        public bool IsConnected
        {
            get
            {
                if (_disposed || _isDisconnectMessageSent || !_isAuthenticated)
                {
                    return false;
                }

                if (_messageListenerCompleted is null || _messageListenerCompleted.WaitOne(0))
                {
                    return false;
                }

                return IsSocketConnected();
            }
        }

        private byte[] _sessionId;

        /// <summary>
        /// Gets the session id.
        /// </summary>
        /// <value>
        /// The session id, or <see langword="null"/> if the client has not been authenticated.
        /// </value>
        public byte[] SessionId
        {
            get
            {
                return _sessionId;
            }
            private set
            {
                _sessionId = value;
                SessionIdHex = ToHex(value);
            }
        }

        internal string SessionIdHex { get; private set; }

        /// <summary>
        /// Gets the client init message.
        /// </summary>
        /// <value>The client init message.</value>
        public KeyExchangeInitMessage ClientInitMessage { get; private set; }

        /// <summary>
        /// Gets the server version string.
        /// </summary>
        /// <value>
        /// The server version.
        /// </value>
        public string ServerVersion { get; private set; }

        /// <summary>
        /// Gets the client version string.
        /// </summary>
        /// <value>
        /// The client version.
        /// </value>
        public string ClientVersion
        {
            get
            {
                return ClientVersionString;
            }
        }

        /// <summary>
        /// Gets the connection info.
        /// </summary>
        /// <value>
        /// The connection info.
        /// </value>
        public ConnectionInfo ConnectionInfo { get; private set; }

        /// <summary>
        /// Occurs when an error occurred.
        /// </summary>
        public event EventHandler<ExceptionEventArgs> ErrorOccured;

        /// <summary>
        /// Occurs when session has been disconnected from the server.
        /// </summary>
        public event EventHandler<EventArgs> Disconnected;

        /// <summary>
        /// Occurs when server identification received.
        /// </summary>
        public event EventHandler<SshIdentificationEventArgs> ServerIdentificationReceived;

        /// <summary>
        /// Occurs when host key received.
        /// </summary>
        public event EventHandler<HostKeyEventArgs> HostKeyReceived;

        /// <summary>
        /// Occurs when <see cref="BannerMessage"/> message is received from the server.
        /// </summary>
        public event EventHandler<MessageEventArgs<BannerMessage>> UserAuthenticationBannerReceived;

        /// <summary>
        /// Occurs when <see cref="InformationRequestMessage"/> message is received from the server.
        /// </summary>
        internal event EventHandler<MessageEventArgs<InformationRequestMessage>> UserAuthenticationInformationRequestReceived;

        /// <summary>
        /// Occurs when <see cref="PasswordChangeRequiredMessage"/> message is received from the server.
        /// </summary>
        internal event EventHandler<MessageEventArgs<PasswordChangeRequiredMessage>> UserAuthenticationPasswordChangeRequiredReceived;

        /// <summary>
        /// Occurs when <see cref="PublicKeyMessage"/> message is received from the server.
        /// </summary>
        internal event EventHandler<MessageEventArgs<PublicKeyMessage>> UserAuthenticationPublicKeyReceived;

        /// <summary>
        /// Occurs when <see cref="KeyExchangeDhGroupExchangeGroup"/> message is received from the server.
        /// </summary>
        internal event EventHandler<MessageEventArgs<KeyExchangeDhGroupExchangeGroup>> KeyExchangeDhGroupExchangeGroupReceived;

        /// <summary>
        /// Occurs when <see cref="KeyExchangeDhGroupExchangeReply"/> message is received from the server.
        /// </summary>
        internal event EventHandler<MessageEventArgs<KeyExchangeDhGroupExchangeReply>> KeyExchangeDhGroupExchangeReplyReceived;

        /// <summary>
        /// Occurs when <see cref="DisconnectMessage"/> message received
        /// </summary>
        internal event EventHandler<MessageEventArgs<DisconnectMessage>> DisconnectReceived;

        /// <summary>
        /// Occurs when <see cref="IgnoreMessage"/> message received
        /// </summary>
        internal event EventHandler<MessageEventArgs<IgnoreMessage>> IgnoreReceived;

        /// <summary>
        /// Occurs when <see cref="UnimplementedMessage"/> message received
        /// </summary>
        internal event EventHandler<MessageEventArgs<UnimplementedMessage>> UnimplementedReceived;

        /// <summary>
        /// Occurs when <see cref="DebugMessage"/> message received
        /// </summary>
        internal event EventHandler<MessageEventArgs<DebugMessage>> DebugReceived;

        /// <summary>
        /// Occurs when <see cref="ServiceRequestMessage"/> message received
        /// </summary>
        internal event EventHandler<MessageEventArgs<ServiceRequestMessage>> ServiceRequestReceived;

        /// <summary>
        /// Occurs when <see cref="ServiceAcceptMessage"/> message received
        /// </summary>
        internal event EventHandler<MessageEventArgs<ServiceAcceptMessage>> ServiceAcceptReceived;

        /// <summary>
        /// Occurs when <see cref="KeyExchangeInitMessage"/> message received
        /// </summary>
        internal event EventHandler<MessageEventArgs<KeyExchangeInitMessage>> KeyExchangeInitReceived;

        /// <summary>
        /// Occurs when a <see cref="KeyExchangeDhReplyMessage"/> message is received from the SSH server.
        /// </summary>
        internal event EventHandler<MessageEventArgs<KeyExchangeDhReplyMessage>> KeyExchangeDhReplyMessageReceived;

        /// <summary>
        /// Occurs when a <see cref="KeyExchangeEcdhReplyMessage"/> message is received from the SSH server.
        /// </summary>
        internal event EventHandler<MessageEventArgs<KeyExchangeEcdhReplyMessage>> KeyExchangeEcdhReplyMessageReceived;

        /// <summary>
        /// Occurs when a <see cref="KeyExchangeHybridReplyMessage"/> message is received from the SSH server.
        /// </summary>
        internal event EventHandler<MessageEventArgs<KeyExchangeHybridReplyMessage>> KeyExchangeHybridReplyMessageReceived;

        /// <summary>
        /// Occurs when <see cref="NewKeysMessage"/> message received
        /// </summary>
        internal event EventHandler<MessageEventArgs<NewKeysMessage>> NewKeysReceived;

        /// <summary>
        /// Occurs when <see cref="RequestMessage"/> message received
        /// </summary>
        internal event EventHandler<MessageEventArgs<RequestMessage>> UserAuthenticationRequestReceived;

        /// <summary>
        /// Occurs when <see cref="FailureMessage"/> message received
        /// </summary>
        internal event EventHandler<MessageEventArgs<FailureMessage>> UserAuthenticationFailureReceived;

        /// <summary>
        /// Occurs when <see cref="SuccessMessage"/> message received
        /// </summary>
        internal event EventHandler<MessageEventArgs<SuccessMessage>> UserAuthenticationSuccessReceived;

        /// <summary>
        /// Occurs when <see cref="RequestSuccessMessage"/> message received
        /// </summary>
        public event EventHandler<MessageEventArgs<RequestSuccessMessage>> RequestSuccessReceived;

        /// <summary>
        /// Occurs when <see cref="RequestFailureMessage"/> message received
        /// </summary>
        public event EventHandler<MessageEventArgs<RequestFailureMessage>> RequestFailureReceived;

        /// <summary>
        /// Occurs when <see cref="ChannelOpenMessage"/> message received
        /// </summary>
        public event EventHandler<MessageEventArgs<ChannelOpenMessage>> ChannelOpenReceived;

        /// <summary>
        /// Occurs when <see cref="ChannelOpenConfirmationMessage"/> message received
        /// </summary>
        public event EventHandler<MessageEventArgs<ChannelOpenConfirmationMessage>> ChannelOpenConfirmationReceived;

        /// <summary>
        /// Occurs when <see cref="ChannelOpenFailureMessage"/> message received
        /// </summary>
        public event EventHandler<MessageEventArgs<ChannelOpenFailureMessage>> ChannelOpenFailureReceived;

        /// <summary>
        /// Occurs when <see cref="ChannelWindowAdjustMessage"/> message received
        /// </summary>
        public event EventHandler<MessageEventArgs<ChannelWindowAdjustMessage>> ChannelWindowAdjustReceived;

        /// <summary>
        /// Occurs when <see cref="ChannelDataMessage"/> message received
        /// </summary>
        public event EventHandler<MessageEventArgs<ChannelDataMessage>> ChannelDataReceived;

        /// <summary>
        /// Occurs when <see cref="ChannelExtendedDataMessage"/> message received
        /// </summary>
        public event EventHandler<MessageEventArgs<ChannelExtendedDataMessage>> ChannelExtendedDataReceived;

        /// <summary>
        /// Occurs when <see cref="ChannelEofMessage"/> message received
        /// </summary>
        public event EventHandler<MessageEventArgs<ChannelEofMessage>> ChannelEofReceived;

        /// <summary>
        /// Occurs when <see cref="ChannelCloseMessage"/> message received
        /// </summary>
        public event EventHandler<MessageEventArgs<ChannelCloseMessage>> ChannelCloseReceived;

        /// <summary>
        /// Occurs when <see cref="ChannelRequestMessage"/> message received
        /// </summary>
        public event EventHandler<MessageEventArgs<ChannelRequestMessage>> ChannelRequestReceived;

        /// <summary>
        /// Occurs when <see cref="ChannelSuccessMessage"/> message received
        /// </summary>
        public event EventHandler<MessageEventArgs<ChannelSuccessMessage>> ChannelSuccessReceived;

        /// <summary>
        /// Occurs when <see cref="ChannelFailureMessage"/> message received
        /// </summary>
        public event EventHandler<MessageEventArgs<ChannelFailureMessage>> ChannelFailureReceived;

        /// <summary>
        /// Initializes a new instance of the <see cref="Session"/> class.
        /// </summary>
        /// <param name="connectionInfo">The connection info.</param>
        /// <param name="serviceFactory">The factory to use for creating new services.</param>
        /// <param name="socketFactory">A factory to create <see cref="Socket"/> instances.</param>
        /// <exception cref="ArgumentNullException"><paramref name="connectionInfo"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="serviceFactory"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="socketFactory"/> is <see langword="null"/>.</exception>
        internal Session(ConnectionInfo connectionInfo, IServiceFactory serviceFactory, ISocketFactory socketFactory)
        {
            ThrowHelper.ThrowIfNull(connectionInfo);
            ThrowHelper.ThrowIfNull(serviceFactory);
            ThrowHelper.ThrowIfNull(socketFactory);

            ConnectionInfo = connectionInfo;
            _serviceFactory = serviceFactory;
            _socketFactory = socketFactory;
            _logger = SshNetLoggingConfiguration.LoggerFactory.CreateLogger<Session>();
            _messageListenerCompleted = new ManualResetEvent(initialState: true);
        }

        /// <summary>
        /// Connects to the server.
        /// </summary>
        /// <exception cref="SocketException">Socket connection to the SSH server or proxy server could not be established, or an error occurred while resolving the hostname.</exception>
        /// <exception cref="SshConnectionException">SSH session could not be established.</exception>
        /// <exception cref="SshAuthenticationException">Authentication of SSH session failed.</exception>
        /// <exception cref="ProxyException">Failed to establish proxy connection.</exception>
        public void Connect()
        {
            if (IsConnected)
            {
                return;
            }

            _connectLock.Wait();

            try
            {
                if (IsConnected)
                {
                    return;
                }

                // Reset connection specific information
                Reset();

                // Build list of available messages while connecting
                _sshMessageFactory = new SshMessageFactory();

                _socket = _serviceFactory.CreateConnector(ConnectionInfo, _socketFactory)
                                            .Connect(ConnectionInfo);

                var serverIdentification = _serviceFactory.CreateProtocolVersionExchange()
                                                            .Start(ClientVersion, _socket, ConnectionInfo.Timeout);

                // Set connection versions
                ServerVersion = ConnectionInfo.ServerVersion = serverIdentification.ToString();
                ConnectionInfo.ClientVersion = ClientVersion;

                _logger.LogInformation("Server version '{ServerIdentification}'.", serverIdentification);

                if (!(serverIdentification.ProtocolVersion.Equals("2.0") || serverIdentification.ProtocolVersion.Equals("1.99")))
                {
                    throw new SshConnectionException(string.Format(CultureInfo.CurrentCulture, "Server version '{0}' is not supported.", serverIdentification.ProtocolVersion),
                                                        DisconnectReason.ProtocolVersionNotSupported);
                }

                ServerIdentificationReceived?.Invoke(this, new SshIdentificationEventArgs(serverIdentification));

                // Register Transport response messages
                RegisterMessage("SSH_MSG_DISCONNECT");
                RegisterMessage("SSH_MSG_IGNORE");
                RegisterMessage("SSH_MSG_UNIMPLEMENTED");
                RegisterMessage("SSH_MSG_DEBUG");
                RegisterMessage("SSH_MSG_SERVICE_ACCEPT");
                RegisterMessage("SSH_MSG_KEXINIT");
                RegisterMessage("SSH_MSG_NEWKEYS");

                // Some server implementations might sent this message first, prior to establishing encryption algorithm
                RegisterMessage("SSH_MSG_USERAUTH_BANNER");

                // Send our key exchange init.
                // We need to do this before starting the message listener to avoid the case where we receive the server
                // key exchange init and we continue the key exchange before having sent our own init.
                _isInitialKex = true;
                ClientInitMessage = BuildClientInitMessage(includeStrictKexPseudoAlgorithm: true);
                SendMessage(ClientInitMessage);

                // Mark the message listener threads as started
                _ = _messageListenerCompleted.Reset();

                // Start incoming request listener
                // ToDo: Make message pump async, to not consume a thread for every session
                _ = ThreadAbstraction.ExecuteThreadLongRunning(MessageListener);

                // Wait for key exchange to be completed
                WaitOnHandle(_keyExchangeCompletedWaitHandle.WaitHandle);

                // If sessionId is not set then its not connected
                if (SessionId is null)
                {
                    Disconnect();
                    return;
                }

                // Request user authorization service
                SendMessage(new ServiceRequestMessage(ServiceName.UserAuthentication));

                // Wait for service to be accepted
                WaitOnHandle(_serviceAccepted);

                if (string.IsNullOrEmpty(ConnectionInfo.Username))
                {
                    throw new SshException("Username is not specified.");
                }

                // Some servers send a global request immediately after successful authentication
                // Avoid race condition by already enabling SSH_MSG_GLOBAL_REQUEST before authentication
                RegisterMessage("SSH_MSG_GLOBAL_REQUEST");

                ConnectionInfo.Authenticate(this, _serviceFactory);
                _isAuthenticated = true;

                // Register Connection messages
                RegisterMessage("SSH_MSG_REQUEST_SUCCESS");
                RegisterMessage("SSH_MSG_REQUEST_FAILURE");
                RegisterMessage("SSH_MSG_CHANNEL_OPEN_CONFIRMATION");
                RegisterMessage("SSH_MSG_CHANNEL_OPEN_FAILURE");
                RegisterMessage("SSH_MSG_CHANNEL_WINDOW_ADJUST");
                RegisterMessage("SSH_MSG_CHANNEL_EXTENDED_DATA");
                RegisterMessage("SSH_MSG_CHANNEL_REQUEST");
                RegisterMessage("SSH_MSG_CHANNEL_SUCCESS");
                RegisterMessage("SSH_MSG_CHANNEL_FAILURE");
                RegisterMessage("SSH_MSG_CHANNEL_DATA");
                RegisterMessage("SSH_MSG_CHANNEL_EOF");
                RegisterMessage("SSH_MSG_CHANNEL_CLOSE");
            }
            finally
            {
                _ = _connectLock.Release();
            }
        }

        /// <summary>
        /// Asynchronously connects to the server.
        /// </summary>
        /// <param name="cancellationToken">The <see cref="CancellationToken"/> to observe.</param>
        /// <returns>A <see cref="Task"/> that represents the asynchronous connect operation.</returns>
        /// <exception cref="SocketException">Socket connection to the SSH server or proxy server could not be established, or an error occurred while resolving the hostname.</exception>
        /// <exception cref="SshConnectionException">SSH session could not be established.</exception>
        /// <exception cref="SshAuthenticationException">Authentication of SSH session failed.</exception>
        /// <exception cref="ProxyException">Failed to establish proxy connection.</exception>
        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            // If connected don't connect again
            if (IsConnected)
            {
                return;
            }

            await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                if (IsConnected)
                {
                    return;
                }

                // Reset connection specific information
                Reset();

                // Build list of available messages while connecting
                _sshMessageFactory = new SshMessageFactory();

                _socket = await _serviceFactory.CreateConnector(ConnectionInfo, _socketFactory)
                                            .ConnectAsync(ConnectionInfo, cancellationToken).ConfigureAwait(false);

                var serverIdentification = await _serviceFactory.CreateProtocolVersionExchange()
                                                            .StartAsync(ClientVersion, _socket, cancellationToken).ConfigureAwait(false);

                // Set connection versions
                ServerVersion = ConnectionInfo.ServerVersion = serverIdentification.ToString();
                ConnectionInfo.ClientVersion = ClientVersion;

                _logger.LogInformation("Server version '{ServerIdentification}'.", serverIdentification);

                if (!(serverIdentification.ProtocolVersion.Equals("2.0") || serverIdentification.ProtocolVersion.Equals("1.99")))
                {
                    throw new SshConnectionException(string.Format(CultureInfo.CurrentCulture, "Server version '{0}' is not supported.", serverIdentification.ProtocolVersion),
                                                        DisconnectReason.ProtocolVersionNotSupported);
                }

                ServerIdentificationReceived?.Invoke(this, new SshIdentificationEventArgs(serverIdentification));

                // Register Transport response messages
                RegisterMessage("SSH_MSG_DISCONNECT");
                RegisterMessage("SSH_MSG_IGNORE");
                RegisterMessage("SSH_MSG_UNIMPLEMENTED");
                RegisterMessage("SSH_MSG_DEBUG");
                RegisterMessage("SSH_MSG_SERVICE_ACCEPT");
                RegisterMessage("SSH_MSG_KEXINIT");
                RegisterMessage("SSH_MSG_NEWKEYS");

                // Some server implementations might sent this message first, prior to establishing encryption algorithm
                RegisterMessage("SSH_MSG_USERAUTH_BANNER");

                // Send our key exchange init.
                // We need to do this before starting the message listener to avoid the case where we receive the server
                // key exchange init and we continue the key exchange before having sent our own init.
                _isInitialKex = true;
                ClientInitMessage = BuildClientInitMessage(includeStrictKexPseudoAlgorithm: true);
                SendMessage(ClientInitMessage);

                // Mark the message listener threads as started
                _ = _messageListenerCompleted.Reset();

                // Start incoming request listener
                // ToDo: Make message pump async, to not consume a thread for every session
                _ = ThreadAbstraction.ExecuteThreadLongRunning(MessageListener);

                // Wait for key exchange to be completed
                WaitOnHandle(_keyExchangeCompletedWaitHandle.WaitHandle);

                // If sessionId is not set then its not connected
                if (SessionId is null)
                {
                    Disconnect();
                    return;
                }

                // Request user authorization service
                SendMessage(new ServiceRequestMessage(ServiceName.UserAuthentication));

                // Wait for service to be accepted
                WaitOnHandle(_serviceAccepted);

                if (string.IsNullOrEmpty(ConnectionInfo.Username))
                {
                    throw new SshException("Username is not specified.");
                }

                // Some servers send a global request immediately after successful authentication
                // Avoid race condition by already enabling SSH_MSG_GLOBAL_REQUEST before authentication
                RegisterMessage("SSH_MSG_GLOBAL_REQUEST");

                ConnectionInfo.Authenticate(this, _serviceFactory);
                _isAuthenticated = true;

                // Register Connection messages
                RegisterMessage("SSH_MSG_REQUEST_SUCCESS");
                RegisterMessage("SSH_MSG_REQUEST_FAILURE");
                RegisterMessage("SSH_MSG_CHANNEL_OPEN_CONFIRMATION");
                RegisterMessage("SSH_MSG_CHANNEL_OPEN_FAILURE");
                RegisterMessage("SSH_MSG_CHANNEL_WINDOW_ADJUST");
                RegisterMessage("SSH_MSG_CHANNEL_EXTENDED_DATA");
                RegisterMessage("SSH_MSG_CHANNEL_REQUEST");
                RegisterMessage("SSH_MSG_CHANNEL_SUCCESS");
                RegisterMessage("SSH_MSG_CHANNEL_FAILURE");
                RegisterMessage("SSH_MSG_CHANNEL_DATA");
                RegisterMessage("SSH_MSG_CHANNEL_EOF");
                RegisterMessage("SSH_MSG_CHANNEL_CLOSE");
            }
            finally
            {
                _ = _connectLock.Release();
            }
        }

        /// <summary>
        /// Disconnects from the server.
        /// </summary>
        /// <remarks>
        /// This sends a <b>SSH_MSG_DISCONNECT</b> message to the server, waits for the
        /// server to close the socket on its end and subsequently closes the client socket.
        /// </remarks>
        public void Disconnect()
        {
            _logger.LogInformation("[{SessionId}] Disconnecting session.", SessionIdHex);

            // send SSH_MSG_DISCONNECT message, clear socket read buffer and dispose it
            Disconnect(DisconnectReason.ByApplication, "Connection terminated by the client.");

            // at this point, we are sure that the listener thread will stop as we've
            // disconnected the socket, so lets wait until the message listener thread
            // has completed
            if (_messageListenerCompleted != null)
            {
                _ = _messageListenerCompleted.WaitOne();
            }
        }

        private void Disconnect(DisconnectReason reason, string message)
        {
            // transition to disconnecting state to avoid throwing exceptions while cleaning up, and to
            // ensure any exceptions that are raised do not overwrite the exception that is set
            _isDisconnecting = true;

            // send disconnect message to the server if the connection is still open
            // and the disconnect message has not yet been sent
            //
            // note that this should also cause the listener loop to be interrupted as
            // the server should respond by closing the socket
            if (IsConnected)
            {
                TrySendDisconnect(reason, message);
            }

            // disconnect socket, and dispose it
            SocketDisconnectAndDispose();
        }

        /// <summary>
        /// Waits for the specified handle or the exception handle for the receive thread
        /// to signal within the connection timeout.
        /// </summary>
        /// <param name="waitHandle">The wait handle.</param>
        /// <exception cref="SshConnectionException">A received package was invalid or failed the message integrity check.</exception>
        /// <exception cref="SshOperationTimeoutException">None of the handles are signaled in time and the session is not disconnecting.</exception>
        /// <exception cref="SocketException">A socket error was signaled while receiving messages from the server.</exception>
        /// <remarks>
        /// When neither handles are signaled in time and the session is not closing, then the
        /// session is disconnected.
        /// </remarks>
        void ISession.WaitOnHandle(WaitHandle waitHandle)
        {
            WaitOnHandle(waitHandle, ConnectionInfo.Timeout);
        }

        /// <summary>
        /// Waits for the specified handle or the exception handle for the receive thread
        /// to signal within the specified timeout.
        /// </summary>
        /// <param name="waitHandle">The wait handle.</param>
        /// <param name="timeout">The time to wait for any of the handles to become signaled.</param>
        /// <exception cref="SshConnectionException">A received package was invalid or failed the message integrity check.</exception>
        /// <exception cref="SshOperationTimeoutException">None of the handles are signaled in time and the session is not disconnecting.</exception>
        /// <exception cref="SocketException">A socket error was signaled while receiving messages from the server.</exception>
        /// <remarks>
        /// When neither handles are signaled in time and the session is not closing, then the
        /// session is disconnected.
        /// </remarks>
        void ISession.WaitOnHandle(WaitHandle waitHandle, TimeSpan timeout)
        {
            WaitOnHandle(waitHandle, timeout);
        }

        /// <summary>
        /// Waits for the specified <seec ref="WaitHandle"/> to receive a signal, using a <see cref="TimeSpan"/>
        /// to specify the time interval.
        /// </summary>
        /// <param name="waitHandle">The <see cref="WaitHandle"/> that should be signaled.</param>
        /// <param name="timeout">A <see cref="TimeSpan"/> that represents the number of milliseconds to wait, or a <see cref="TimeSpan"/> that represents <c>-1</c> milliseconds to wait indefinitely.</param>
        /// <returns>
        /// A <see cref="WaitResult"/>.
        /// </returns>
        WaitResult ISession.TryWait(WaitHandle waitHandle, TimeSpan timeout)
        {
            return TryWait(waitHandle, timeout, out _);
        }

        /// <summary>
        /// Waits for the specified <seec ref="WaitHandle"/> to receive a signal, using a <see cref="TimeSpan"/>
        /// to specify the time interval.
        /// </summary>
        /// <param name="waitHandle">The <see cref="WaitHandle"/> that should be signaled.</param>
        /// <param name="timeout">A <see cref="TimeSpan"/> that represents the number of milliseconds to wait, or a <see cref="TimeSpan"/> that represents <c>-1</c> milliseconds to wait indefinitely.</param>
        /// <param name="exception">When this method returns <see cref="WaitResult.Failed"/>, contains the <see cref="Exception"/>.</param>
        /// <returns>
        /// A <see cref="WaitResult"/>.
        /// </returns>
        WaitResult ISession.TryWait(WaitHandle waitHandle, TimeSpan timeout, out Exception exception)
        {
            return TryWait(waitHandle, timeout, out exception);
        }

        /// <summary>
        /// Waits for the specified <seec ref="WaitHandle"/> to receive a signal, using a <see cref="TimeSpan"/>
        /// to specify the time interval.
        /// </summary>
        /// <param name="waitHandle">The <see cref="WaitHandle"/> that should be signaled.</param>
        /// <param name="timeout">A <see cref="TimeSpan"/> that represents the number of milliseconds to wait, or a <see cref="TimeSpan"/> that represents <c>-1</c> milliseconds to wait indefinitely.</param>
        /// <param name="exception">When this method returns <see cref="WaitResult.Failed"/>, contains the <see cref="Exception"/>.</param>
        /// <returns>
        /// A <see cref="WaitResult"/>.
        /// </returns>
        private WaitResult TryWait(WaitHandle waitHandle, TimeSpan timeout, out Exception exception)
        {
            ThrowHelper.ThrowIfNull(waitHandle);

            var waitHandles = new[]
                {
                    _exceptionWaitHandle,
                    _messageListenerCompleted,
                    waitHandle
                };

            switch (WaitHandle.WaitAny(waitHandles, timeout))
            {
                case 0:
                    if (_exception is SshConnectionException)
                    {
                        exception = null;
                        return WaitResult.Disconnected;
                    }

                    exception = _exception;
                    return WaitResult.Failed;
                case 1:
                    exception = null;
                    return WaitResult.Disconnected;
                case 2:
                    exception = null;
                    return WaitResult.Success;
                case WaitHandle.WaitTimeout:
                    exception = null;
                    return WaitResult.TimedOut;
                default:
                    throw new InvalidOperationException("Unexpected result.");
            }
        }

        /// <summary>
        /// Waits for the specified handle or the exception handle for the receive thread
        /// to signal within the connection timeout.
        /// </summary>
        /// <param name="waitHandle">The wait handle.</param>
        /// <exception cref="SshConnectionException">A received package was invalid or failed the message integrity check.</exception>
        /// <exception cref="SshOperationTimeoutException">None of the handles are signaled in time and the session is not disconnecting.</exception>
        /// <exception cref="SocketException">A socket error was signaled while receiving messages from the server.</exception>
        /// <remarks>
        /// When neither handles are signaled in time and the session is not closing, then the
        /// session is disconnected.
        /// </remarks>
        internal void WaitOnHandle(WaitHandle waitHandle)
        {
            WaitOnHandle(waitHandle, ConnectionInfo.Timeout);
        }

        /// <summary>
        /// Waits for the specified handle or the exception handle for the receive thread
        /// to signal within the specified timeout.
        /// </summary>
        /// <param name="waitHandle">The wait handle.</param>
        /// <param name="timeout">The time to wait for any of the handles to become signaled.</param>
        /// <exception cref="SshConnectionException">A received package was invalid or failed the message integrity check.</exception>
        /// <exception cref="SshOperationTimeoutException">None of the handles are signaled in time and the session is not disconnecting.</exception>
        /// <exception cref="SocketException">A socket error was signaled while receiving messages from the server.</exception>
        internal void WaitOnHandle(WaitHandle waitHandle, TimeSpan timeout)
        {
            ThrowHelper.ThrowIfNull(waitHandle);

            var waitHandles = new[]
                {
                    _exceptionWaitHandle,
                    _messageListenerCompleted,
                    waitHandle
                };

            var signaledElement = WaitHandle.WaitAny(waitHandles, timeout);
            switch (signaledElement)
            {
                case 0:
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(_exception).Throw();
                    break;
                case 1:
                    throw new SshConnectionException("Client not connected.");
                case 2:
                    // Specified waithandle was signaled
                    break;
                case WaitHandle.WaitTimeout:
                    // when the session is disconnecting, a timeout is likely when no
                    // network connectivity is available; depending on the configured
                    // timeout either the WaitAny times out first or a SocketException
                    // detailing a timeout thrown hereby completing the listener thread
                    // (which makes us end up in case 1). Either way, we do not want to
                    // report an exception to the client when we're disconnecting anyway
                    if (!_isDisconnecting)
                    {
                        throw new SshOperationTimeoutException("Session operation has timed out");
                    }

                    break;
                default:
                    throw new SshException($"Unexpected element '{signaledElement.ToString(CultureInfo.InvariantCulture)}' signaled.");
            }
        }

        /// <summary>
        /// Sends a message to the server.
        /// </summary>
        /// <param name="message">The message to send.</param>
        /// <exception cref="SshConnectionException">The client is not connected.</exception>
        /// <exception cref="SshOperationTimeoutException">The operation timed out.</exception>
        /// <exception cref="InvalidOperationException">The size of the packet exceeds the maximum size defined by the protocol.</exception>
        internal void SendMessage(Message message)
        {
            if (!_socket.CanWrite())
            {
                throw new SshConnectionException("Client not connected.");
            }

            if (!_keyExchangeCompletedWaitHandle.IsSet && message is not IKeyExchangedAllowed)
            {
                // Wait for key exchange to be completed
                WaitOnHandle(_keyExchangeCompletedWaitHandle.WaitHandle);
            }

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace("[{SessionId}] Sending message {MessageName}({MessageNumber}) to server: '{Message}'.", SessionIdHex, message.MessageName, message.MessageNumber, message.ToString());
            }

            var paddingMultiplier = _clientCipher is null ? (byte)8 : Math.Max((byte)8, _clientCipher.MinimumSize);
            var packetData = message.GetPacket(paddingMultiplier, _clientCompression, _clientEtm || _clientAead);

            // take a write lock to ensure the outbound packet sequence number is incremented
            // atomically, and only after the packet has actually been sent
            lock (_socketWriteLock)
            {
                byte[] hash = null;
                var packetDataOffset = 4; // first four bytes are reserved for outbound packet sequence

                // write outbound packet sequence to start of packet data
                BinaryPrimitives.WriteUInt32BigEndian(packetData, _outboundPacketSequence);

                if (_clientMac != null && !_clientEtm)
                {
                    // calculate packet hash
                    hash = _clientMac.ComputeHash(packetData);
                }

                // Encrypt packet data
                if (_clientCipher != null)
                {
                    _clientCipher.SetSequenceNumber(_outboundPacketSequence);
                    if (_clientEtm)
                    {
                        // The length of the "packet length" field in bytes
                        const int packetLengthFieldLength = 4;

                        var encryptedData = _clientCipher.Encrypt(packetData, packetDataOffset + packetLengthFieldLength, packetData.Length - packetDataOffset - packetLengthFieldLength);

                        Array.Resize(ref packetData, packetDataOffset + packetLengthFieldLength + encryptedData.Length);

                        // write encrypted data
                        Buffer.BlockCopy(encryptedData, 0, packetData, packetDataOffset + packetLengthFieldLength, encryptedData.Length);

                        // calculate packet hash
                        hash = _clientMac.ComputeHash(packetData);
                    }
                    else
                    {
                        packetData = _clientCipher.Encrypt(packetData, packetDataOffset, packetData.Length - packetDataOffset);
                        packetDataOffset = 0;
                    }
                }

                if (packetData.Length > MaximumSshPacketSize)
                {
                    throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, "Packet is too big. Maximum packet size is {0} bytes.", MaximumSshPacketSize));
                }

                var packetLength = packetData.Length - packetDataOffset;
                if (hash is null)
                {
                    SendPacket(packetData, packetDataOffset, packetLength);
                }
                else
                {
                    var data = new byte[packetLength + hash.Length];
                    Buffer.BlockCopy(packetData, packetDataOffset, data, 0, packetLength);
                    Buffer.BlockCopy(hash, 0, data, packetLength, hash.Length);
                    SendPacket(data, 0, data.Length);
                }

                if (_isStrictKex && message is NewKeysMessage)
                {
                    _outboundPacketSequence = 0;
                }
                else
                {
                    // increment the packet sequence number only after we're sure the packet has
                    // been sent; even though it's only used for the MAC, it needs to be incremented
                    // for each package sent.
                    //
                    // the server will use it to verify the data integrity, and as such the order in
                    // which messages are sent must follow the outbound packet sequence number
                    _outboundPacketSequence++;
                }
            }
        }

        /// <summary>
        /// Sends an SSH packet to the server.
        /// </summary>
        /// <param name="packet">A byte array containing the packet to send.</param>
        /// <param name="offset">The offset of the packet.</param>
        /// <param name="length">The length of the packet.</param>
        /// <exception cref="SshConnectionException">Client is not connected to the server.</exception>
        /// <remarks>
        /// <para>
        /// The send is performed in a dispose lock to avoid <see cref="NullReferenceException"/>
        /// and/or <see cref="ObjectDisposedException"/> when sending the packet.
        /// </para>
        /// <para>
        /// This method is only to be used when the connection is established, as the locking
        /// overhead is not required while establishing the connection.
        /// </para>
        /// </remarks>
        private void SendPacket(byte[] packet, int offset, int length)
        {
            _socketDisposeLock.Wait();

            try
            {
                if (!_socket.IsConnected())
                {
                    throw new SshConnectionException("Client not connected.");
                }

                SocketAbstraction.Send(_socket, packet, offset, length);
            }
            finally
            {
                _ = _socketDisposeLock.Release();
            }
        }

        /// <summary>
        /// Sends a message to the server.
        /// </summary>
        /// <param name="message">The message to send.</param>
        /// <returns>
        /// <see langword="true"/> if the message was sent to the server; otherwise, <see langword="false"/>.
        /// </returns>
        /// <exception cref="InvalidOperationException">The size of the packet exceeds the maximum size defined by the protocol.</exception>
        /// <remarks>
        /// This methods returns <see langword="false"/> when the attempt to send the message results in a
        /// <see cref="SocketException"/> or a <see cref="SshException"/>.
        /// </remarks>
        private bool TrySendMessage(Message message)
        {
            try
            {
                SendMessage(message);
                return true;
            }
            catch (SshException ex)
            {
                _logger.LogInformation(ex, "Failure sending message {MessageName}({MessageNumber}) to server: '{Message}'", message.MessageName, message.MessageNumber, message.ToString());
                return false;
            }
            catch (SocketException ex)
            {
                _logger.LogInformation(ex, "Failure sending message {MessageName}({MessageNumber}) to server: '{Message}'", message.MessageName, message.MessageNumber, message.ToString());
                return false;
            }
        }

        /// <summary>
        /// Receives the message from the server.
        /// </summary>
        /// <returns>
        /// The incoming SSH message, or <see langword="null"/> if the connection with the SSH server was closed.
        /// </returns>
        /// <remarks>
        /// We need no locking here since all messages are read by a single thread.
        /// </remarks>
        private Message ReceiveMessage(Socket socket)
        {
            // the length of the packet sequence field in bytes
            const int inboundPacketSequenceLength = 4;

            // The length of the "packet length" field in bytes
            const int packetLengthFieldLength = 4;

            // The length of the "padding length" field in bytes
            const int paddingLengthFieldLength = 1;

            int blockSize;

            // Determine the size of the first block which is 8 or cipher block size (whichever is larger) bytes, or 4 if "packet length" field is handled separately.
            if (_serverEtm || _serverAead)
            {
                blockSize = (byte)4;
            }
            else if (_serverCipher != null)
            {
                blockSize = Math.Max((byte)8, _serverCipher.MinimumSize);
            }
            else
            {
                blockSize = (byte)8;
            }

            var serverMacLength = 0;

            if (_serverAead)
            {
                serverMacLength = _serverCipher.TagSize;
            }
            else if (_serverMac != null)
            {
                serverMacLength = _serverMac.HashSize / 8;
            }

            byte[] data;
            uint packetLength;

            // avoid reading from socket while IsSocketConnected is attempting to determine whether the
            // socket is still connected by invoking Socket.Poll(...) and subsequently verifying value of
            // Socket.Available
            lock (_socketReadLock)
            {
                // Read first block - which starts with the packet length
                var firstBlock = new byte[blockSize];
                if (TrySocketRead(socket, firstBlock, 0, blockSize) == 0)
                {
                    // connection with SSH server was closed
                    return null;
                }

                var plainFirstBlock = firstBlock;

                // First block is not encrypted in AES GCM mode.
                if (_serverCipher is not null and not Security.Cryptography.Ciphers.AesGcmCipher)
                {
                    _serverCipher.SetSequenceNumber(_inboundPacketSequence);

                    // First block is not encrypted in ETM mode.
                    if (_serverMac == null || !_serverEtm)
                    {
                        plainFirstBlock = _serverCipher.Decrypt(firstBlock);
                    }
                }

                packetLength = BinaryPrimitives.ReadUInt32BigEndian(plainFirstBlock);

                // Test packet minimum and maximum boundaries
                if (packetLength < Math.Max((byte)8, blockSize) - 4 || packetLength > MaximumSshPacketSize - 4)
                {
                    throw new SshConnectionException(string.Format(CultureInfo.CurrentCulture, "Bad packet length: {0}.", packetLength),
                                                     DisconnectReason.ProtocolError);
                }

                // Determine the number of bytes left to read; We've already read "blockSize" bytes, but the
                // "packet length" field itself - which is 4 bytes - is not included in the length of the packet
                var bytesToRead = (int)(packetLength - (blockSize - packetLengthFieldLength)) + serverMacLength;

                // Construct buffer for holding the payload and the inbound packet sequence as we need both in order
                // to generate the hash.
                //
                // The total length of the "data" buffer is an addition of:
                // - inboundPacketSequenceLength (4 bytes)
                // - packetLength
                // - serverMacLength
                //
                // We include the inbound packet sequence to allow us to have the the full SSH packet in a single
                // byte[] for the purpose of calculating the client hash. Room for the server MAC is foreseen
                // to read the packet including server MAC in a single pass (except for the initial block).
                data = new byte[bytesToRead + blockSize + inboundPacketSequenceLength];
                BinaryPrimitives.WriteUInt32BigEndian(data, _inboundPacketSequence);

                // Use raw packet length field to calculate the mac in AEAD mode.
                if (_serverAead)
                {
                    Buffer.BlockCopy(firstBlock, 0, data, inboundPacketSequenceLength, blockSize);
                }
                else
                {
                    Buffer.BlockCopy(plainFirstBlock, 0, data, inboundPacketSequenceLength, blockSize);
                }

                if (bytesToRead > 0)
                {
                    if (TrySocketRead(socket, data, blockSize + inboundPacketSequenceLength, bytesToRead) == 0)
                    {
                        return null;
                    }
                }
            }

            // validate encrypted message against MAC
            if (_serverMac != null && _serverEtm)
            {
                var clientHash = _serverMac.ComputeHash(data, 0, data.Length - serverMacLength);
#if NETSTANDARD2_1 || NET
                if (!CryptographicOperations.FixedTimeEquals(clientHash, new ReadOnlySpan<byte>(data, data.Length - serverMacLength, serverMacLength)))
#else
                if (!Org.BouncyCastle.Utilities.Arrays.FixedTimeEquals(serverMacLength, clientHash, 0, data, data.Length - serverMacLength))
#endif
                {
                    throw new SshConnectionException("MAC error", DisconnectReason.MacError);
                }
            }

            if (_serverCipher != null)
            {
                var numberOfBytesToDecrypt = data.Length - (blockSize + inboundPacketSequenceLength + serverMacLength);
                if (numberOfBytesToDecrypt > 0)
                {
                    var decryptedData = _serverCipher.Decrypt(data, blockSize + inboundPacketSequenceLength, numberOfBytesToDecrypt);
                    Buffer.BlockCopy(decryptedData, 0, data, blockSize + inboundPacketSequenceLength, decryptedData.Length);
                }
            }

            var paddingLength = data[inboundPacketSequenceLength + packetLengthFieldLength];
            var messagePayloadLength = (int)packetLength - paddingLength - paddingLengthFieldLength;
            var messagePayloadOffset = inboundPacketSequenceLength + packetLengthFieldLength + paddingLengthFieldLength;

            // validate decrypted message against MAC
            if (_serverMac != null && !_serverEtm)
            {
                var clientHash = _serverMac.ComputeHash(data, 0, data.Length - serverMacLength);
#if NETSTANDARD2_1 || NET
                if (!CryptographicOperations.FixedTimeEquals(clientHash, new ReadOnlySpan<byte>(data, data.Length - serverMacLength, serverMacLength)))
#else
                if (!Org.BouncyCastle.Utilities.Arrays.FixedTimeEquals(serverMacLength, clientHash, 0, data, data.Length - serverMacLength))
#endif
                {
                    throw new SshConnectionException("MAC error", DisconnectReason.MacError);
                }
            }

            if (_serverDecompression != null)
            {
                data = _serverDecompression.Decompress(data, messagePayloadOffset, messagePayloadLength);

                // Data now only contains the decompressed payload, and as such the offset is reset to zero
                messagePayloadOffset = 0;

                // The length of the payload is now the complete decompressed content
                messagePayloadLength = data.Length;
            }

            _inboundPacketSequence++;

            // The below code mirrors from https://github.com/openssh/openssh-portable/commit/1edb00c58f8a6875fad6a497aa2bacf37f9e6cd5
            // It ensures the integrity of key exchange process.
            if (_inboundPacketSequence == uint.MaxValue && _isInitialKex)
            {
                throw new SshConnectionException("Inbound packet sequence number is about to wrap during initial key exchange.", DisconnectReason.KeyExchangeFailed);
            }

            return LoadMessage(data, messagePayloadOffset, messagePayloadLength);
        }

        private void TrySendDisconnect(DisconnectReason reasonCode, string message)
        {
            var disconnectMessage = new DisconnectMessage(reasonCode, message);

            // Send the disconnect message, but ignore the outcome
            _ = TrySendMessage(disconnectMessage);

            // Mark disconnect message sent regardless of whether the send sctually succeeded
            _isDisconnectMessageSent = true;
        }

        /// <summary>
        /// Called when <see cref="DisconnectMessage"/> received.
        /// </summary>
        /// <param name="message"><see cref="DisconnectMessage"/> message.</param>
        internal void OnDisconnectReceived(DisconnectMessage message)
        {
            _logger.LogInformation("[{SessionId}] Disconnect received: {ReasonCode} {MessageDescription}.", SessionIdHex, message.ReasonCode, message.Description);

            // transition to disconnecting state to avoid throwing exceptions while cleaning up, and to
            // ensure any exceptions that are raised do not overwrite the SshConnectionException that we
            // set below
            _isDisconnecting = true;

            _exception = new SshConnectionException(string.Format(CultureInfo.InvariantCulture, "The connection was closed by the server: {0} ({1}).", message.Description, message.ReasonCode), message.ReasonCode);
            _ = _exceptionWaitHandle.Set();

            DisconnectReceived?.Invoke(this, new MessageEventArgs<DisconnectMessage>(message));

            Disconnected?.Invoke(this, EventArgs.Empty);

            // disconnect socket, and dispose it
            SocketDisconnectAndDispose();
        }

        /// <summary>
        /// Called when <see cref="IgnoreMessage"/> received.
        /// </summary>
        /// <param name="message"><see cref="IgnoreMessage"/> message.</param>
        internal void OnIgnoreReceived(IgnoreMessage message)
        {
            IgnoreReceived?.Invoke(this, new MessageEventArgs<IgnoreMessage>(message));
        }

        /// <summary>
        /// Called when <see cref="UnimplementedMessage"/> message received.
        /// </summary>
        /// <param name="message"><see cref="UnimplementedMessage"/> message.</param>
        internal void OnUnimplementedReceived(UnimplementedMessage message)
        {
            UnimplementedReceived?.Invoke(this, new MessageEventArgs<UnimplementedMessage>(message));
        }

        /// <summary>
        /// Called when <see cref="DebugMessage"/> message received.
        /// </summary>
        /// <param name="message"><see cref="DebugMessage"/> message.</param>
        internal void OnDebugReceived(DebugMessage message)
        {
            DebugReceived?.Invoke(this, new MessageEventArgs<DebugMessage>(message));
        }

        /// <summary>
        /// Called when <see cref="ServiceRequestMessage"/> message received.
        /// </summary>
        /// <param name="message"><see cref="ServiceRequestMessage"/> message.</param>
        internal void OnServiceRequestReceived(ServiceRequestMessage message)
        {
            ServiceRequestReceived?.Invoke(this, new MessageEventArgs<ServiceRequestMessage>(message));
        }

        /// <summary>
        /// Called when <see cref="ServiceAcceptMessage"/> message received.
        /// </summary>
        /// <param name="message"><see cref="ServiceAcceptMessage"/> message.</param>
        internal void OnServiceAcceptReceived(ServiceAcceptMessage message)
        {
            ServiceAcceptReceived?.Invoke(this, new MessageEventArgs<ServiceAcceptMessage>(message));

            _ = _serviceAccepted.Set();
        }

        internal void OnKeyExchangeDhGroupExchangeGroupReceived(KeyExchangeDhGroupExchangeGroup message)
        {
            KeyExchangeDhGroupExchangeGroupReceived?.Invoke(this, new MessageEventArgs<KeyExchangeDhGroupExchangeGroup>(message));
        }

        internal void OnKeyExchangeDhGroupExchangeReplyReceived(KeyExchangeDhGroupExchangeReply message)
        {
            KeyExchangeDhGroupExchangeReplyReceived?.Invoke(this, new MessageEventArgs<KeyExchangeDhGroupExchangeReply>(message));
        }

        /// <summary>
        /// Called when <see cref="KeyExchangeInitMessage"/> message received.
        /// </summary>
        /// <param name="message"><see cref="KeyExchangeInitMessage"/> message.</param>
        internal void OnKeyExchangeInitReceived(KeyExchangeInitMessage message)
        {
            // If _keyExchangeCompletedWaitHandle is already set, then this is a key
            // re-exchange initiated by the server, and we need to send our own init
            // message.
            // Otherwise, the wait handle is not set and this received init is part of the
            // initial connection for which we have already sent our init, so we shouldn't
            // send another one.
            var sendClientInitMessage = _keyExchangeCompletedWaitHandle.IsSet;

            _keyExchangeCompletedWaitHandle.Reset();

            if (_isInitialKex && message.KeyExchangeAlgorithms.Contains("kex-strict-s-v00@openssh.com"))
            {
                _isStrictKex = true;

                _logger.LogDebug("[{SessionId}] Enabling strict key exchange extension.", SessionIdHex);

                if (_inboundPacketSequence != 1)
                {
                    throw new SshConnectionException("KEXINIT was not the first packet during strict key exchange.", DisconnectReason.KeyExchangeFailed);
                }
            }

            // Disable messages that are not key exchange related
            _sshMessageFactory.DisableNonKeyExchangeMessages(_isStrictKex);

            _keyExchange = _serviceFactory.CreateKeyExchange(ConnectionInfo.KeyExchangeAlgorithms,
                                                             message.KeyExchangeAlgorithms);

            ConnectionInfo.CurrentKeyExchangeAlgorithm = _keyExchange.Name;

            _logger.LogDebug("[{SessionId}] Performing {KeyExchangeAlgorithm} key exchange.", SessionIdHex, ConnectionInfo.CurrentKeyExchangeAlgorithm);

            _keyExchange.HostKeyReceived += KeyExchange_HostKeyReceived;

            // Start the algorithm implementation
            _keyExchange.Start(this, message, sendClientInitMessage);

            KeyExchangeInitReceived?.Invoke(this, new MessageEventArgs<KeyExchangeInitMessage>(message));
        }

        internal void OnKeyExchangeDhReplyMessageReceived(KeyExchangeDhReplyMessage message)
        {
            KeyExchangeDhReplyMessageReceived?.Invoke(this, new MessageEventArgs<KeyExchangeDhReplyMessage>(message));
        }

        internal void OnKeyExchangeEcdhReplyMessageReceived(KeyExchangeEcdhReplyMessage message)
        {
            KeyExchangeEcdhReplyMessageReceived?.Invoke(this, new MessageEventArgs<KeyExchangeEcdhReplyMessage>(message));
        }

        internal void OnKeyExchangeHybridReplyMessageReceived(KeyExchangeHybridReplyMessage message)
        {
            KeyExchangeHybridReplyMessageReceived?.Invoke(this, new MessageEventArgs<KeyExchangeHybridReplyMessage>(message));
        }

        /// <summary>
        /// Called when <see cref="NewKeysMessage"/> message received.
        /// </summary>
        /// <param name="message"><see cref="NewKeysMessage"/> message.</param>
        internal void OnNewKeysReceived(NewKeysMessage message)
        {
            // Update sessionId
            SessionId ??= _keyExchange.ExchangeHash;

            // Dispose of old ciphers and hash algorithms
            if (_serverCipher is IDisposable disposableServerCipher)
            {
                disposableServerCipher.Dispose();
            }

            if (_clientCipher is IDisposable disposableClientCipher)
            {
                disposableClientCipher.Dispose();
            }

            if (_serverMac != null)
            {
                _serverMac.Dispose();
                _serverMac = null;
            }

            if (_clientMac != null)
            {
                _clientMac.Dispose();
                _clientMac = null;
            }

            // Update negotiated algorithms
            _serverCipher = _keyExchange.CreateServerCipher(out _serverAead);
            _clientCipher = _keyExchange.CreateClientCipher(out _clientAead);

            _serverMac = _keyExchange.CreateServerHash(out _serverEtm);
            _clientMac = _keyExchange.CreateClientHash(out _clientEtm);

            _clientCompression = _keyExchange.CreateCompressor();
            _serverDecompression = _keyExchange.CreateDecompressor();

#if DEBUG
            if (SshNetLoggingConfiguration.WiresharkKeyLogFilePath is string path
                && _keyExchange is KeyExchange kex)
            {
                System.IO.File.AppendAllText(
                    path,
                    $"{ToHex(ClientInitMessage.Cookie)} SHARED_SECRET {ToHex(kex.SharedKey)}{Environment.NewLine}");
            }
#endif

            // Dispose of old KeyExchange object as it is no longer needed.
            _keyExchange.HostKeyReceived -= KeyExchange_HostKeyReceived;
            _keyExchange.Dispose();
            _keyExchange = null;

            // Enable activated messages that are not key exchange related
            _sshMessageFactory.EnableActivatedMessages();

            if (_isInitialKex)
            {
                _isInitialKex = false;
                ClientInitMessage = BuildClientInitMessage(includeStrictKexPseudoAlgorithm: false);
            }

            if (_isStrictKex)
            {
                _inboundPacketSequence = 0;
            }

            NewKeysReceived?.Invoke(this, new MessageEventArgs<NewKeysMessage>(message));

            // Signal that key exchange completed
            _keyExchangeCompletedWaitHandle.Set();
        }

        /// <summary>
        /// Called when client is disconnecting from the server.
        /// </summary>
        void ISession.OnDisconnecting()
        {
            _isDisconnecting = true;
        }

        /// <summary>
        /// Called when <see cref="RequestMessage"/> message received.
        /// </summary>
        /// <param name="message"><see cref="RequestMessage"/> message.</param>
        internal void OnUserAuthenticationRequestReceived(RequestMessage message)
        {
            UserAuthenticationRequestReceived?.Invoke(this, new MessageEventArgs<RequestMessage>(message));
        }

        /// <summary>
        /// Called when <see cref="FailureMessage"/> message received.
        /// </summary>
        /// <param name="message"><see cref="FailureMessage"/> message.</param>
        internal void OnUserAuthenticationFailureReceived(FailureMessage message)
        {
            UserAuthenticationFailureReceived?.Invoke(this, new MessageEventArgs<FailureMessage>(message));
        }

        /// <summary>
        /// Called when <see cref="SuccessMessage"/> message received.
        /// </summary>
        /// <param name="message"><see cref="SuccessMessage"/> message.</param>
        internal void OnUserAuthenticationSuccessReceived(SuccessMessage message)
        {
            UserAuthenticationSuccessReceived?.Invoke(this, new MessageEventArgs<SuccessMessage>(message));
        }

        /// <summary>
        /// Called when <see cref="BannerMessage"/> message received.
        /// </summary>
        /// <param name="message"><see cref="BannerMessage"/> message.</param>
        internal void OnUserAuthenticationBannerReceived(BannerMessage message)
        {
            UserAuthenticationBannerReceived?.Invoke(this, new MessageEventArgs<BannerMessage>(message));
        }

        /// <summary>
        /// Called when <see cref="InformationRequestMessage"/> message received.
        /// </summary>
        /// <param name="message"><see cref="InformationRequestMessage"/> message.</param>
        internal void OnUserAuthenticationInformationRequestReceived(InformationRequestMessage message)
        {
            UserAuthenticationInformationRequestReceived?.Invoke(this, new MessageEventArgs<InformationRequestMessage>(message));
        }

        internal void OnUserAuthenticationPasswordChangeRequiredReceived(PasswordChangeRequiredMessage message)
        {
            UserAuthenticationPasswordChangeRequiredReceived?.Invoke(this, new MessageEventArgs<PasswordChangeRequiredMessage>(message));
        }

        internal void OnUserAuthenticationPublicKeyReceived(PublicKeyMessage message)
        {
            UserAuthenticationPublicKeyReceived?.Invoke(this, new MessageEventArgs<PublicKeyMessage>(message));
        }

        /// <summary>
        /// Called when <see cref="GlobalRequestMessage"/> message received.
        /// </summary>
        /// <param name="message"><see cref="GlobalRequestMessage"/> message.</param>
        internal void OnGlobalRequestReceived(GlobalRequestMessage message)
        {
            if (message.WantReply)
            {
                SendMessage(new RequestFailureMessage());
            }
        }

        /// <summary>
        /// Called when <see cref="RequestSuccessMessage"/> message received.
        /// </summary>
        /// <param name="message"><see cref="RequestSuccessMessage"/> message.</param>
        internal void OnRequestSuccessReceived(RequestSuccessMessage message)
        {
            RequestSuccessReceived?.Invoke(this, new MessageEventArgs<RequestSuccessMessage>(message));
        }

        /// <summary>
        /// Called when <see cref="RequestFailureMessage"/> message received.
        /// </summary>
        /// <param name="message"><see cref="RequestFailureMessage"/> message.</param>
        internal void OnRequestFailureReceived(RequestFailureMessage message)
        {
            RequestFailureReceived?.Invoke(this, new MessageEventArgs<RequestFailureMessage>(message));
        }

        /// <summary>
        /// Called when <see cref="ChannelOpenMessage"/> message received.
        /// </summary>
        /// <param name="message"><see cref="ChannelOpenMessage"/> message.</param>
        internal void OnChannelOpenReceived(ChannelOpenMessage message)
        {
            ChannelOpenReceived?.Invoke(this, new MessageEventArgs<ChannelOpenMessage>(message));
        }

        /// <summary>
        /// Called when <see cref="ChannelOpenConfirmationMessage"/> message received.
        /// </summary>
        /// <param name="message"><see cref="ChannelOpenConfirmationMessage"/> message.</param>
        internal void OnChannelOpenConfirmationReceived(ChannelOpenConfirmationMessage message)
        {
            ChannelOpenConfirmationReceived?.Invoke(this, new MessageEventArgs<ChannelOpenConfirmationMessage>(message));
        }

        /// <summary>
        /// Called when <see cref="ChannelOpenFailureMessage"/> message received.
        /// </summary>
        /// <param name="message"><see cref="ChannelOpenFailureMessage"/> message.</param>
        internal void OnChannelOpenFailureReceived(ChannelOpenFailureMessage message)
        {
            ChannelOpenFailureReceived?.Invoke(this, new MessageEventArgs<ChannelOpenFailureMessage>(message));
        }

        /// <summary>
        /// Called when <see cref="ChannelWindowAdjustMessage"/> message received.
        /// </summary>
        /// <param name="message"><see cref="ChannelWindowAdjustMessage"/> message.</param>
        internal void OnChannelWindowAdjustReceived(ChannelWindowAdjustMessage message)
        {
            ChannelWindowAdjustReceived?.Invoke(this, new MessageEventArgs<ChannelWindowAdjustMessage>(message));
        }

        /// <summary>
        /// Called when <see cref="ChannelDataMessage"/> message received.
        /// </summary>
        /// <param name="message"><see cref="ChannelDataMessage"/> message.</param>
        internal void OnChannelDataReceived(ChannelDataMessage message)
        {
            ChannelDataReceived?.Invoke(this, new MessageEventArgs<ChannelDataMessage>(message));
        }

        /// <summary>
        /// Called when <see cref="ChannelExtendedDataMessage"/> message received.
        /// </summary>
        /// <param name="message"><see cref="ChannelExtendedDataMessage"/> message.</param>
        internal void OnChannelExtendedDataReceived(ChannelExtendedDataMessage message)
        {
            ChannelExtendedDataReceived?.Invoke(this, new MessageEventArgs<ChannelExtendedDataMessage>(message));
        }

        /// <summary>
        /// Called when <see cref="ChannelCloseMessage"/> message received.
        /// </summary>
        /// <param name="message"><see cref="ChannelCloseMessage"/> message.</param>
        internal void OnChannelEofReceived(ChannelEofMessage message)
        {
            ChannelEofReceived?.Invoke(this, new MessageEventArgs<ChannelEofMessage>(message));
        }

        /// <summary>
        /// Called when <see cref="ChannelCloseMessage"/> message received.
        /// </summary>
        /// <param name="message"><see cref="ChannelCloseMessage"/> message.</param>
        internal void OnChannelCloseReceived(ChannelCloseMessage message)
        {
            ChannelCloseReceived?.Invoke(this, new MessageEventArgs<ChannelCloseMessage>(message));
        }

        /// <summary>
        /// Called when <see cref="ChannelRequestMessage"/> message received.
        /// </summary>
        /// <param name="message"><see cref="ChannelRequestMessage"/> message.</param>
        internal void OnChannelRequestReceived(ChannelRequestMessage message)
        {
            ChannelRequestReceived?.Invoke(this, new MessageEventArgs<ChannelRequestMessage>(message));
        }

        /// <summary>
        /// Called when <see cref="ChannelSuccessMessage"/> message received.
        /// </summary>
        /// <param name="message"><see cref="ChannelSuccessMessage"/> message.</param>
        internal void OnChannelSuccessReceived(ChannelSuccessMessage message)
        {
            ChannelSuccessReceived?.Invoke(this, new MessageEventArgs<ChannelSuccessMessage>(message));
        }

        /// <summary>
        /// Called when <see cref="ChannelFailureMessage"/> message received.
        /// </summary>
        /// <param name="message"><see cref="ChannelFailureMessage"/> message.</param>
        internal void OnChannelFailureReceived(ChannelFailureMessage message)
        {
            ChannelFailureReceived?.Invoke(this, new MessageEventArgs<ChannelFailureMessage>(message));
        }

        private void KeyExchange_HostKeyReceived(object sender, HostKeyEventArgs e)
        {
            HostKeyReceived?.Invoke(this, e);
        }

        /// <summary>
        /// Registers SSH message with the session.
        /// </summary>
        /// <param name="messageName">The name of the message to register with the session.</param>
        public void RegisterMessage(string messageName)
        {
            _sshMessageFactory.EnableAndActivateMessage(messageName);
        }

        /// <summary>
        /// Unregister SSH message from the session.
        /// </summary>
        /// <param name="messageName">The name of the message to unregister with the session.</param>
        public void UnRegisterMessage(string messageName)
        {
            _sshMessageFactory.DisableAndDeactivateMessage(messageName);
        }

        /// <summary>
        /// Loads a message from a given buffer.
        /// </summary>
        /// <param name="data">An array of bytes from which to construct the message.</param>
        /// <param name="offset">The zero-based byte offset in <paramref name="data"/> at which to begin reading.</param>
        /// <param name="count">The number of bytes to load.</param>
        /// <returns>
        /// A message constructed from <paramref name="data"/>.
        /// </returns>
        /// <exception cref="SshException">The type of the message is not supported.</exception>
        private Message LoadMessage(byte[] data, int offset, int count)
        {
            var messageType = data[offset];

            var message = _sshMessageFactory.Create(messageType);
            message.Load(data, offset + 1, count - 1);

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                _logger.LogTrace("[{SessionId}] Received message {MessageName}({MessageNumber}) from server: '{Message}'.", SessionIdHex, message.MessageName, message.MessageNumber, message.ToString());
            }

            return message;
        }

        private static string ToHex(byte[] bytes)
        {
            if (bytes is null)
            {
                return null;
            }

#if NET
            return Convert.ToHexString(bytes);
#else
            var builder = new StringBuilder(bytes.Length * 2);

            foreach (var b in bytes)
            {
                builder.Append(b.ToString("X2"));
            }

            return builder.ToString();
#endif
        }

        /// <summary>
        /// Gets a value indicating whether the socket is connected.
        /// </summary>
        /// <returns>
        /// <see langword="true"/> if the socket is connected; otherwise, <see langword="false"/>.
        /// </returns>
        /// <remarks>
        /// <para>
        /// As a first check we verify whether <see cref="Socket.Connected"/> is
        /// <see langword="true"/>. However, this only returns the state of the socket as of
        /// the last I/O operation.
        /// </para>
        /// <para>
        /// Therefore we use the combination of <see cref="Socket.Poll(int, SelectMode)"/> with mode <see cref="SelectMode.SelectRead"/>
        /// and <see cref="Socket.Available"/> to verify if the socket is still connected.
        /// </para>
        /// <para>
        /// The MSDN doc mention the following on the return value of <see cref="Socket.Poll(int, SelectMode)"/>
        /// with mode <see cref="SelectMode.SelectRead"/>:
        /// <list type="bullet">
        ///     <item>
        ///         <description><see langword="true"/> if data is available for reading;</description>
        ///     </item>
        ///     <item>
        ///         <description><see langword="true"/> if the connection has been closed, reset, or terminated; otherwise, returns <see langword="false"/>.</description>
        ///     </item>
        /// </list>
        /// </para>
        /// <para>
        /// <c>Conclusion:</c> when the return value is <see langword="true"/> - but no data is available for reading - then
        /// the socket is no longer connected.
        /// </para>
        /// <para>
        /// When a <see cref="Socket"/> is used from multiple threads, there's a race condition
        /// between the invocation of <see cref="Socket.Poll(int, SelectMode)"/> and the moment
        /// when the value of <see cref="Socket.Available"/> is obtained. To workaround this issue
        /// we synchronize reads from the <see cref="Socket"/>.
        /// </para>
        /// <para>
        /// We assume the socket is still connected if the read lock cannot be acquired immediately.
        /// In this case, we just return <see langword="true"/> without actually waiting to acquire
        /// the lock. We don't want to wait for the read lock if another thread already has it because
        /// there are cases where the other thread holding the lock can be waiting indefinitely for
        /// a socket read operation to complete.
        /// </para>
        /// </remarks>
        private bool IsSocketConnected()
        {
            _socketDisposeLock.Wait();

            try
            {
                if (!_socket.IsConnected())
                {
                    return false;
                }

                if (!_socketReadLock.TryEnter())
                {
                    return true;
                }

                try
                {
                    var connectionClosedOrDataAvailable = _socket.Poll(0, SelectMode.SelectRead);
                    return !(connectionClosedOrDataAvailable && _socket.Available == 0);
                }
                finally
                {
                    _socketReadLock.Exit();
                }
            }
            finally
            {
                _ = _socketDisposeLock.Release();
            }
        }

        /// <summary>
        /// Performs a blocking read on the socket until <paramref name="length"/> bytes are received.
        /// </summary>
        /// <param name="socket">The <see cref="Socket"/> to read from.</param>
        /// <param name="buffer">An array of type <see cref="byte"/> that is the storage location for the received data.</param>
        /// <param name="offset">The position in <paramref name="buffer"/> parameter to store the received data.</param>
        /// <param name="length">The number of bytes to read.</param>
        /// <returns>
        /// The number of bytes read.
        /// </returns>
        /// <exception cref="SshOperationTimeoutException">The read has timed-out.</exception>
        /// <exception cref="SocketException">The read failed.</exception>
        private static int TrySocketRead(Socket socket, byte[] buffer, int offset, int length)
        {
            return SocketAbstraction.Read(socket, buffer, offset, length, Timeout.InfiniteTimeSpan);
        }

        /// <summary>
        /// Shuts down and disposes the socket.
        /// </summary>
        private void SocketDisconnectAndDispose()
        {
            if (_socket != null)
            {
                _socketDisposeLock.Wait();

                try
                {
#pragma warning disable CA1508 // Avoid dead conditional code; Value could have been changed by another thread.
                    if (_socket != null)
#pragma warning restore CA1508 // Avoid dead conditional code
                    {
                        if (_socket.Connected)
                        {
                            try
                            {
                                _logger.LogDebug("[{SessionId}] Shutting down socket.", SessionIdHex);

                                // Interrupt any pending reads; should be done outside of socket read lock as we
                                // actually want shutdown the socket to make sure blocking reads are interrupted.
                                //
                                // This may result in a SocketException (eg. An existing connection was forcibly
                                // closed by the remote host) which we'll log and ignore as it means the socket
                                // was already shut down.
                                _socket.Shutdown(SocketShutdown.Send);
                            }
                            catch (SocketException ex)
                            {
                                _logger.LogInformation(ex, "Failure shutting down socket");
                            }
                        }

                        _logger.LogDebug("[{SessionId}] Disposing socket.", SessionIdHex);
                        _socket.Dispose();
                        _logger.LogDebug("[{SessionId}] Disposed socket.", SessionIdHex);
                        _socket = null;
                    }
                }
                finally
                {
                    _ = _socketDisposeLock.Release();
                }
            }
        }

        /// <summary>
        /// Listens for incoming message from the server and handles them. This method run as a task on separate thread.
        /// </summary>
        private void MessageListener()
        {
            try
            {
                // remain in message loop until socket is shut down or until we're disconnecting
                while (true)
                {
                    var socket = _socket;

                    if (socket is null || !socket.Connected)
                    {
                        break;
                    }

                    try
                    {
                        // Block until either data is available or the socket is closed
                        var connectionClosedOrDataAvailable = socket.Poll(-1, SelectMode.SelectRead);
                        if (connectionClosedOrDataAvailable && socket.Available == 0)
                        {
                            // connection with SSH server was closed or connection was reset
                            break;
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        // The socket was disposed by either:
                        // * a call to Disconnect()
                        // * a call to Dispose()
                        // * a SSH_MSG_DISCONNECT received from server
                        break;
                    }

                    var message = ReceiveMessage(socket);
                    if (message is null)
                    {
                        // Connection with SSH server was closed, so break out of the message loop
                        break;
                    }

                    // process message
                    message.Process(this);
                }

                // connection with SSH server was closed or socket was disposed
                RaiseError(CreateConnectionAbortedByServerException());
            }
            catch (SocketException ex)
            {
                RaiseError(new SshConnectionException(ex.Message, DisconnectReason.ConnectionLost, ex));
            }
            catch (Exception exp)
            {
                RaiseError(exp);
            }
            finally
            {
                // signal that the message listener thread has stopped
                _ = _messageListenerCompleted.Set();
            }
        }

        /// <summary>
        /// Raises the <see cref="ErrorOccured"/> event.
        /// </summary>
        /// <param name="exp">The <see cref="Exception"/>.</param>
        private void RaiseError(Exception exp)
        {
            var connectionException = exp as SshConnectionException;

            _logger.LogInformation(exp, "[{SessionId}] Raised exception", SessionIdHex);

            if (_isDisconnecting)
            {
                // a connection exception which is raised while isDisconnecting is normal and
                // should be ignored
                if (connectionException != null)
                {
                    return;
                }

                // any timeout while disconnecting can be caused by loss of connectivity
                // altogether and should be ignored
                if (exp is SocketException socketException && socketException.SocketErrorCode == SocketError.TimedOut)
                {
                    return;
                }
            }

            // "save" exception and set exception wait handle to ensure any waits are interrupted
            _exception = exp;
            _ = _exceptionWaitHandle.Set();

            ErrorOccured?.Invoke(this, new ExceptionEventArgs(exp));

            if (connectionException != null)
            {
                _logger.LogInformation(exp, "[{SessionId}] Disconnecting after exception", SessionIdHex);
                Disconnect(connectionException.DisconnectReason, exp.ToString());
            }
        }

        /// <summary>
        /// Resets connection-specific information to ensure state of a previous connection
        /// does not affect new connections.
        /// </summary>
        private void Reset()
        {
            _ = _exceptionWaitHandle?.Reset();
            _keyExchangeCompletedWaitHandle?.Reset();
            _ = _messageListenerCompleted?.Set();

            SessionId = null;
            _isDisconnectMessageSent = false;
            _isDisconnecting = false;
            _isAuthenticated = false;
            _exception = null;
        }

        private static SshConnectionException CreateConnectionAbortedByServerException()
        {
            return new SshConnectionException("An established connection was aborted by the server.",
            DisconnectReason.ConnectionLost);
        }

        private KeyExchangeInitMessage BuildClientInitMessage(bool includeStrictKexPseudoAlgorithm)
        {
            return new KeyExchangeInitMessage
            {
                KeyExchangeAlgorithms = includeStrictKexPseudoAlgorithm ?
                                        ConnectionInfo.KeyExchangeAlgorithms.Keys.Concat(["kex-strict-c-v00@openssh.com"]).ToArray() :
                                        ConnectionInfo.KeyExchangeAlgorithms.Keys.ToArray(),
                ServerHostKeyAlgorithms = ConnectionInfo.HostKeyAlgorithms.Keys.ToArray(),
                EncryptionAlgorithmsClientToServer = ConnectionInfo.Encryptions.Keys.ToArray(),
                EncryptionAlgorithmsServerToClient = ConnectionInfo.Encryptions.Keys.ToArray(),
                MacAlgorithmsClientToServer = ConnectionInfo.HmacAlgorithms.Keys.ToArray(),
                MacAlgorithmsServerToClient = ConnectionInfo.HmacAlgorithms.Keys.ToArray(),
                CompressionAlgorithmsClientToServer = ConnectionInfo.CompressionAlgorithms.Keys.ToArray(),
                CompressionAlgorithmsServerToClient = ConnectionInfo.CompressionAlgorithms.Keys.ToArray(),
                LanguagesClientToServer = new[] { string.Empty },
                LanguagesServerToClient = new[] { string.Empty },
                FirstKexPacketFollows = false,
                Reserved = 0,
            };
        }

        private bool _disposed;

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="disposing"><see langword="true"/> to release both managed and unmanaged resources; <see langword="false"/> to release only unmanaged resources.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                _logger.LogDebug("[{SessionId}] Disposing session.", SessionIdHex);

                Disconnect();

                var serviceAccepted = _serviceAccepted;
                if (serviceAccepted != null)
                {
                    serviceAccepted.Dispose();
                    _serviceAccepted = null;
                }

                var exceptionWaitHandle = _exceptionWaitHandle;
                if (exceptionWaitHandle != null)
                {
                    exceptionWaitHandle.Dispose();
                    _exceptionWaitHandle = null;
                }

                var keyExchangeCompletedWaitHandle = _keyExchangeCompletedWaitHandle;
                if (keyExchangeCompletedWaitHandle != null)
                {
                    keyExchangeCompletedWaitHandle.Dispose();
                    _keyExchangeCompletedWaitHandle = null;
                }

                if (_serverCipher is IDisposable disposableServerCipher)
                {
                    disposableServerCipher.Dispose();
                }

                if (_clientCipher is IDisposable disposableClientCipher)
                {
                    disposableClientCipher.Dispose();
                }

                var serverMac = _serverMac;
                if (serverMac != null)
                {
                    serverMac.Dispose();
                    _serverMac = null;
                }

                var clientMac = _clientMac;
                if (clientMac != null)
                {
                    clientMac.Dispose();
                    _clientMac = null;
                }

                var serverDecompression = _serverDecompression;
                if (serverDecompression != null)
                {
                    serverDecompression.Dispose();
                    _serverDecompression = null;
                }

                var clientCompression = _clientCompression;
                if (clientCompression != null)
                {
                    clientCompression.Dispose();
                    _clientCompression = null;
                }

                var keyExchange = _keyExchange;
                if (keyExchange != null)
                {
                    keyExchange.HostKeyReceived -= KeyExchange_HostKeyReceived;
                    keyExchange.Dispose();
                    _keyExchange = null;
                }

                var messageListenerCompleted = _messageListenerCompleted;
                if (messageListenerCompleted != null)
                {
                    messageListenerCompleted.Dispose();
                    _messageListenerCompleted = null;
                }

                _disposed = true;
            }
        }

        /// <summary>
        /// Gets the connection info.
        /// </summary>
        /// <value>The connection info.</value>
        IConnectionInfo ISession.ConnectionInfo
        {
            get { return ConnectionInfo; }
        }

        /// <summary>
        /// Gets a <see cref="WaitHandle"/> that can be used to wait for the message listener loop to complete.
        /// </summary>
        /// <value>
        /// A <see cref="WaitHandle"/> that can be used to wait for the message listener loop to complete, or
        /// <see langword="null"/> when the session has not been connected.
        /// </value>
        WaitHandle ISession.MessageListenerCompleted
        {
            get { return _messageListenerCompleted; }
        }

        /// <summary>
        /// Create a new SSH session channel.
        /// </summary>
        /// <returns>
        /// A new SSH session channel.
        /// </returns>
        IChannelSession ISession.CreateChannelSession()
        {
            return new ChannelSession(this, NextChannelNumber, InitialLocalWindowSize, LocalChannelDataPacketSize);
        }

        /// <summary>
        /// Create a new channel for a locally forwarded TCP/IP port.
        /// </summary>
        /// <returns>
        /// A new channel for a locally forwarded TCP/IP port.
        /// </returns>
        IChannelDirectTcpip ISession.CreateChannelDirectTcpip()
        {
            return new ChannelDirectTcpip(this, NextChannelNumber, InitialLocalWindowSize, LocalChannelDataPacketSize);
        }

        /// <summary>
        /// Creates a "forwarded-tcpip" SSH channel.
        /// </summary>
        /// <param name="remoteChannelNumber">The number of the remote channel.</param>
        /// <param name="remoteWindowSize">The window size of the remote channel.</param>
        /// <param name="remoteChannelDataPacketSize">The data packet size of the remote channel.</param>
        /// <returns>
        /// A new "forwarded-tcpip" SSH channel.
        /// </returns>
        IChannelForwardedTcpip ISession.CreateChannelForwardedTcpip(uint remoteChannelNumber,
                                                                    uint remoteWindowSize,
                                                                    uint remoteChannelDataPacketSize)
        {
            return new ChannelForwardedTcpip(this,
                                             NextChannelNumber,
                                             InitialLocalWindowSize,
                                             LocalChannelDataPacketSize,
                                             remoteChannelNumber,
                                             remoteWindowSize,
                                             remoteChannelDataPacketSize);
        }

        /// <summary>
        /// Sends a message to the server.
        /// </summary>
        /// <param name="message">The message to send.</param>
        /// <exception cref="SshConnectionException">The client is not connected.</exception>
        /// <exception cref="SshOperationTimeoutException">The operation timed out.</exception>
        /// <exception cref="InvalidOperationException">The size of the packet exceeds the maximum size defined by the protocol.</exception>
        void ISession.SendMessage(Message message)
        {
            SendMessage(message);
        }

        /// <summary>
        /// Sends a message to the server.
        /// </summary>
        /// <param name="message">The message to send.</param>
        /// <returns>
        /// <see langword="true"/> if the message was sent to the server; otherwise, <see langword="false"/>.
        /// </returns>
        /// <exception cref="InvalidOperationException">The size of the packet exceeds the maximum size defined by the protocol.</exception>
        /// <remarks>
        /// This methods returns <see langword="false"/> when the attempt to send the message results in a
        /// <see cref="SocketException"/> or a <see cref="SshException"/>.
        /// </remarks>
        bool ISession.TrySendMessage(Message message)
        {
            return TrySendMessage(message);
        }
    }

    /// <summary>
    /// Represents the result of a wait operations.
    /// </summary>
    internal enum WaitResult
    {
        /// <summary>
        /// The <see cref="WaitHandle"/> was signaled within the specified interval.
        /// </summary>
        Success = 1,

        /// <summary>
        /// The <see cref="WaitHandle"/> was not signaled within the specified interval.
        /// </summary>
        TimedOut = 2,

        /// <summary>
        /// The session is in a disconnected state.
        /// </summary>
        Disconnected = 3,

        /// <summary>
        /// The session is in a failed state.
        /// </summary>
        Failed = 4
    }
}
