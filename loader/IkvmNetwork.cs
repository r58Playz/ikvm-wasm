using System;
using System.IO;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using java.net;
using javax.net.ssl;
using java.security.cert;
using X509Certificate = javax.security.cert.X509Certificate;
using Principal = java.security.Principal;
using Security = java.security.Security;
using SecProvider = java.security.Provider;

/// <summary>
/// Adapts a <see cref="ClientWebSocket"/> to full <see cref="Stream"/> semantics,
/// treating the WebSocket as a duplex byte stream (binary messages).
/// Each Write call is sent as one WebSocket message (or chunked to a max frame size);
/// reads reassemble across message boundaries as an endless byte stream.
/// </summary>
public sealed class WebSocketStream : Stream
{
	private readonly ClientWebSocket _socket;
	private readonly bool _ownsSocket;
	private readonly int _maxWriteChunk;

	// Buffer to hold leftover bytes from a WebSocket message that
	// didn't fully fit in the caller's read buffer.
	private readonly byte[] _readBuffer;
	private int _readOffset;
	private int _readCount;
	private bool _readClosed;

	public WebSocketStream(ClientWebSocket socket, bool ownsSocket = true, int maxWriteChunk = 64 * 1024)
	{
		_socket = socket ?? throw new ArgumentNullException(nameof(socket));
		_ownsSocket = ownsSocket;
		_maxWriteChunk = maxWriteChunk;
		_readBuffer = new byte[8192];
		_readOffset = 0;
		_readCount = 0;
	}

	public override bool CanRead => _socket.State == WebSocketState.Open || _readCount > 0;
	public override bool CanWrite => _socket.State == WebSocketState.Open;
	public override bool CanSeek => false;
	public override bool CanTimeout => true;

	public override long Length => throw new NotSupportedException("WebSocketStream does not support Length.");
	public override long Position
	{
		get => throw new NotSupportedException("WebSocketStream does not support Position.");
		set => throw new NotSupportedException("WebSocketStream does not support Position.");
	}

	public override long Seek(long offset, SeekOrigin origin) =>
		throw new NotSupportedException("WebSocketStream does not support seeking.");

	public override void SetLength(long value) =>
		throw new NotSupportedException("WebSocketStream does not support SetLength.");

	// ---------- Reading ----------

	public override int Read(byte[] buffer, int offset, int count)
		=> ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

	public override int Read(Span<byte> buffer)
	{
		// Fallback: rent an array-backed copy path via ReadAsync overload
		var rented = new byte[buffer.Length];
		int n = Read(rented, 0, rented.Length);
		rented.AsSpan(0, n).CopyTo(buffer);
		return n;
	}

	public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
	{
		ValidateBufferArgs(buffer, offset, count);
		if (count == 0) return 0;

		// Serve from any leftover buffered bytes first.
		if (_readCount > 0)
		{
			int n = Math.Min(count, _readCount);
			Array.Copy(_readBuffer, _readOffset, buffer, offset, n);
			_readOffset += n;
			_readCount -= n;
			return n;
		}

		if (_readClosed)
			return 0;

		while (true)
		{
			WebSocketReceiveResult result;
			try
			{
				result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer, offset, count), cancellationToken)
					.ConfigureAwait(false);
			}
			catch (WebSocketException wex)
			{
				throw new System.IO.IOException("WebSocket read failed.", wex);
			}

			if (result.MessageType == WebSocketMessageType.Close)
			{
				_readClosed = true;
				try
				{
					await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ack", cancellationToken)
						.ConfigureAwait(false);
				}
				catch { /* best-effort */ }
				return 0;
			}

			if (result.Count == 0 && !result.EndOfMessage)
			{
				// Rare: zero-byte fragment, keep reading.
				continue;
			}

			return result.Count;
		}
	}

	// ---------- Writing ----------

	public override void Write(byte[] buffer, int offset, int count)
		=> WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

	public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
	{
		ValidateBufferArgs(buffer, offset, count);
		if (count == 0) return;

		int written = 0;
		while (written < count)
		{
			int chunk = Math.Min(_maxWriteChunk, count - written);
			bool isLast = (written + chunk) >= count;

			try
			{
				await _socket.SendAsync(
					new ArraySegment<byte>(buffer, offset + written, chunk),
					WebSocketMessageType.Binary,
					endOfMessage: isLast,
					cancellationToken).ConfigureAwait(false);
			}
			catch (WebSocketException wex)
			{
				throw new System.IO.IOException("WebSocket write failed.", wex);
			}

			written += chunk;
		}
	}

	public override void Flush()
	{
		// WebSocket sends are not buffered on this side; nothing to flush.
	}

	public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

	// ---------- Close/Dispose ----------

	public override void Close()
	{
		try
		{
			if (_socket.State == WebSocketState.Open || _socket.State == WebSocketState.CloseReceived)
			{
				_socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Stream closed", CancellationToken.None)
					.GetAwaiter().GetResult();
			}
		}
		catch { /* ignore close race/errors */ }
		finally
		{
			base.Close();
		}
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			try
			{
				if (_socket.State == WebSocketState.Open || _socket.State == WebSocketState.CloseReceived)
				{
					_socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Stream disposed", CancellationToken.None)
						.GetAwaiter().GetResult();
				}
			}
			catch { /* ignore */ }

			if (_ownsSocket)
				_socket.Dispose();
		}
		base.Dispose(disposing);
	}

#if NET
	public override async ValueTask DisposeAsync()
	{
		try
		{
			if (_socket.State == WebSocketState.Open || _socket.State == WebSocketState.CloseReceived)
			{
				await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Stream disposed", CancellationToken.None)
					.ConfigureAwait(false);
			}
		}
		catch { /* ignore */ }

		if (_ownsSocket)
			_socket.Dispose();

		await base.DisposeAsync().ConfigureAwait(false);
	}
#endif

	private static void ValidateBufferArgs(byte[] buffer, int offset, int count)
	{
		if (buffer == null) throw new ArgumentNullException(nameof(buffer));
		if (offset < 0 || count < 0 || offset + count > buffer.Length)
			throw new ArgumentOutOfRangeException(nameof(count));
	}
}

static class IkvmNetworkState
{
	static readonly List<string> FakeDnsResolves = [];

	private static (byte, byte, byte, byte) GetIPFromIndex(int index)
	{
		if (index / byte.MaxValue > byte.MaxValue) throw new ArgumentOutOfRangeException("index too large");
		if (index < 0) throw new ArgumentOutOfRangeException("index below zero");

		return (240, 67, (byte)(index / byte.MaxValue), (byte)(index % byte.MaxValue));
	}
	private static int GetIndexFromIP((byte, byte, byte, byte) ip)
	{
		if (ip.Item1 != 240 || ip.Item2 != 67) throw new ArgumentOutOfRangeException("not a fake ip");
		return ip.Item3 * byte.MaxValue + ip.Item4;
	}

	internal static (byte, byte, byte, byte) GetFakeIP(string host)
	{
		lock (FakeDnsResolves)
		{
			var idx = FakeDnsResolves.FindIndex(x => x == host);
			if (idx < 0)
			{
				idx = FakeDnsResolves.Count;
				FakeDnsResolves.Add(host);
			}
			return GetIPFromIndex(idx);
		}
	}

	internal static string GetHostFromFakeIP((byte, byte, byte, byte) ip)
	{
		lock (FakeDnsResolves)
		{
			return FakeDnsResolves[GetIndexFromIP(ip)];
		}
	}

	// Map an address back to the original hostname: one of IkvmNameService's synthetic 240.67.x.y
	// IPs resolves to the host it was minted for; anything else (e.g. a literal IP that never went
	// through our resolver) falls back to its own hostname.
	internal static string ResolveHost(InetAddress addr)
	{
		var ip = addr.getAddress();
		if (ip != null && ip.Length == 4)
		{
			try { return GetHostFromFakeIP((ip[0], ip[1], ip[2], ip[3])); }
			catch { }
		}
		return addr.getHostName();
	}

	// Resolve a socket address to the (host, port) the proxy should dial.
	internal static (string host, int port) ResolveTarget(SocketAddress remote)
	{
		var isa = (InetSocketAddress)remote;
		int port = isa.getPort();
		var addr = isa.getAddress();
		return (addr != null ? ResolveHost(addr) : isa.getHostString(), port);
	}
}

// JDK 18+ resolves names through an InetAddressResolver (sun.net.spi.nameservice is gone): the one InetAddress has
// already chosen when its resolver field is set, before the first lookup.
public class IkvmNameService : java.net.spi.InetAddressResolver
{
	// The machine's own name (gethostname), which InetAddress.getLocalHost() resolves through us.
	private static string LocalHostName;

	public static void Install()
	{
		_ = InetAddress.getByAddress([240, 0, 0, 0]);

		var impl = typeof(InetAddress).GetField("impl", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
		LocalHostName = (string)impl.GetType().GetMethod("getLocalHostName").Invoke(impl, null);

		var field = typeof(InetAddress).GetField("resolver", BindingFlags.Static | BindingFlags.NonPublic);
		field.SetValue(null, new IkvmNameService());
	}

	public java.util.stream.Stream lookupByName(string host, java.net.spi.InetAddressResolver.LookupPolicy lookupPolicy)
	{
		if (host.Equals(LocalHostName, StringComparison.OrdinalIgnoreCase))
			return java.util.Arrays.stream(LocalHostAddresses(host));

		var (a, b, c, d) = IkvmNetworkState.GetFakeIP(host);
		return java.util.Arrays.stream(new object[] { InetAddress.getByAddress([a, b, c, d]) });
	}

	// The local host is the machine's interfaces (libnet's synthetic eth0, see IkvmNetworkInterfaces), not a fake IP:
	// that would be numbered by what happened to be resolved first, and differ from one launch to the next.
	private static object[] LocalHostAddresses(string host)
	{
		var addrs = new List<object>();
		try
		{
			var nifs = NetworkInterface.getNetworkInterfaces();
			while (nifs.hasMoreElements())
			{
				var nif = (NetworkInterface)nifs.nextElement();
				if (nif.isLoopback() || !nif.isUp())
					continue;
				var inets = nif.getInetAddresses();
				while (inets.hasMoreElements())
					addrs.Add(InetAddress.getByAddress(host, ((InetAddress)inets.nextElement()).getAddress()));
			}
		}
		catch (SocketException e)
		{
			Console.WriteLine($"[IkvmNameService] no network interfaces for the local host: {e.getMessage()}");
		}

		if (addrs.Count == 0)
			addrs.Add(InetAddress.getByAddress(host, InetAddress.getLoopbackAddress().getAddress()));
		return addrs.ToArray();
	}

	public string lookupByAddress(byte[] addr)
	{
		var address = (addr[0], addr[1], addr[2], addr[3]);
		try
		{
			return IkvmNetworkState.GetHostFromFakeIP(address);
		}
		catch (Exception e)
		{
			Console.WriteLine($"[IkvmNameService] failed to resolve [{string.Join(", ", addr)}] {e}");
		}
		throw new UnknownHostException();
	}
}

// IKVM's libnet reports a synthetic eth0 under emscripten and reads its MAC from sysfs, as Linux does
// (emscripten-stubs/ikvm_netif.h in ikvm-wasm-build). This demo uses an in-memory filesystem, so keep a locally
// administered address for the lifetime of the page.
public static class IkvmNetworkInterfaces
{
	private const string SysfsAddressPath = "/sys/class/net/eth0/address";

	public static void Install()
	{
		if (!File.Exists(SysfsAddressPath))
		{
			var mac = System.Security.Cryptography.RandomNumberGenerator.GetBytes(6);
			mac[0] = (byte)((mac[0] & 0xfc) | 0x02); // unicast, locally administered
			Directory.CreateDirectory(Path.GetDirectoryName(SysfsAddressPath));
			File.WriteAllText(SysfsAddressPath, BitConverter.ToString(mac).Replace('-', ':').ToLowerInvariant() + "\n");
		}
	}
}

public static class IkvmSocketService
{
	public static void Install()
	{
		Socket.setSocketImplFactory(new EpoxySocketImplFactory());
		DatagramSocket.setDatagramSocketImplFactory(new NoDatagramSocketImplFactory());
		Security.insertProviderAt(new EpoxyProvider(), 1);
		HttpsURLConnection.setDefaultSSLSocketFactory(new EpoxySslSocketFactory());
		HttpsURLConnection.setDefaultHostnameVerifier(new AllowAllHostnameVerifier());
	}

	private class AllowAllHostnameVerifier : HostnameVerifier
	{
		public bool verify(string hostname, SSLSession session) => true;
	}

	private class EpoxyProvider : SecProvider
	{
		private class EpoxyService(SecProvider p, string algo) : Service(p, "SSLContext", algo, "cli.IkvmSocketService$EpoxySslContextSpi", null, null)
		{
            public override object newInstance(object constructorParameter)
            {
				return new EpoxySslContextSpi();
            }
		}

		public EpoxyProvider() : base("Epoxy", 1.0, "epoxy-tls socket backend")
		{
            foreach (var algo in new[] { "TLS", "TLSv1.2", "TLSv1.3", "SSL", "Default" })
            {
                putService(new EpoxyService(this, algo));
            }
		}
	}

	private class EpoxySslContextSpi : SSLContextSpi
	{
		private SSLSocketFactory SocketFactory;

        protected override void engineInit(KeyManager[] km, TrustManager[] tm, java.security.SecureRandom sr)
        {
			SocketFactory = new EpoxySslSocketFactory();
        }

        protected override SSLSocketFactory engineGetSocketFactory() => SocketFactory;
        protected override SSLServerSocketFactory engineGetServerSocketFactory() => throw new NotSupportedException("no server sockets");
        protected override SSLEngine engineCreateSSLEngine() => throw new NotSupportedException("epoxy handles ssl internally");
        protected override SSLEngine engineCreateSSLEngine(string host, int port) => throw new NotSupportedException("epoxy handles ssl internally");
        protected override SSLSessionContext engineGetClientSessionContext() => null;
        protected override SSLSessionContext engineGetServerSessionContext() => null;
	}

	private class EpoxySocketImplFactory : SocketImplFactory
	{
		public SocketImpl createSocketImpl() => new EpoxySocketImpl();
	}

	private class EpoxyInputStream(WebSocketStream Socket) : java.io.InputStream
	{
		private readonly Lock lck = new();

		public override int read()
		{
			lock (lck)
			{
				return Socket.ReadByte();
			}
		}

		public override int read(byte[] b)
		{
			lock (lck)
			{
				var n = Socket.Read(b);
				return n <= 0 ? -1 : n;
			}
		}

		public override int read(byte[] b, int off, int len)
		{
			if (len == 0) return 0;
			lock (lck)
			{
				var n = Socket.Read(b, off, len);
				return n <= 0 ? -1 : n;
			}
		}
	}
	private class EpoxyOutputStream(WebSocketStream Socket) : java.io.OutputStream
	{
		private readonly Lock lck = new();

		public override void write(int b)
		{
			lock (lck)
			{
				Socket.WriteByte((byte)b);
			}
		}

		public override void write(byte[] b)
		{
			lock (lck)
			{
				Socket.Write(b);
			}
		}

		public override void write(byte[] b, int off, int len)
		{
			lock (lck)
			{
				Socket.Write(b, off, len);
			}
		}
	}

	private class EpoxySocketImpl : SocketImpl
	{
		private string Protocol;
		private WebSocketStream Socket;

		protected override void create(bool stream)
		{
			Protocol = stream ? "wisp-tcp" : "wisp-udp";
		}

		protected override void connect(SocketAddress address, int timeout)
		{
			var (host, port) = IkvmNetworkState.ResolveTarget(address);

			ClientWebSocket socket = new();
			var task = socket.ConnectAsync(new($"ws://{Protocol}/{host}:{port}"), new());
			if (timeout == 0)
			{
				task.Wait();
			}
			else
			{
				if (!task.Wait(TimeSpan.FromMilliseconds(timeout)))
					throw new TimeoutException("connect took too long");
			}

			Socket = new(socket);
		}

		protected override java.io.InputStream getInputStream() => new EpoxyInputStream(Socket);
		protected override java.io.OutputStream getOutputStream() => new EpoxyOutputStream(Socket);
		protected override void close()
		{
			lock (Socket)
			{
				Socket.Close();
			}
		}

		protected override int available() => 0;

		protected override void connect(InetAddress address, int port) => connect(new InetSocketAddress(address, port), 30 * 1000);
		protected override void connect(string host, int port) => connect(new InetSocketAddress(host, port), 30 * 1000);

		public override void setOption(int optID, object value) { /* tunnel has no knobs; ignore */ }
		public override object getOption(int optID) => optID == SocketOptions.SO_TIMEOUT ? 0 : null; // sane defaults
		protected override void sendUrgentData(int data) => throw new NotImplementedException();

		protected override void bind(InetAddress host, int port) => throw new NotImplementedException("no servers");
		protected override void listen(int backlog) => throw new NotImplementedException("no servers");
		protected override void accept(SocketImpl s) => throw new NotImplementedException("no servers");
	}

	// The wisp proxy carries TCP streams only, so there is no UDP. Left to the JDK, a DatagramSocket is a DatagramChannel,
	// whose dispatcher cannot even initialize under emscripten (socketpair fails): creating one threw NoClassDefFoundError,
	// an Error that callers' catch (Exception) lets through. Fail creation with a SocketException instead.
	private class NoDatagramSocketImplFactory : DatagramSocketImplFactory
	{
		public DatagramSocketImpl createDatagramSocketImpl() => new NoDatagramSocketImpl();
	}

	private class NoDatagramSocketImpl : DatagramSocketImpl
	{
		private static SocketException Unsupported() => new("UDP is not supported: the wisp proxy only carries TCP");

		protected override void create() => throw Unsupported();
		protected override void close() { } // DatagramSocket closes the delegate whose creation failed

		protected override void bind(int lport, InetAddress laddr) => throw Unsupported();
		protected override void send(DatagramPacket p) => throw Unsupported();
		protected override int peek(InetAddress i) => throw Unsupported();
		protected override int peekData(DatagramPacket p) => throw Unsupported();
		protected override void receive(DatagramPacket p) => throw Unsupported();
		[Obsolete] protected override void setTTL(byte ttl) => throw Unsupported();
		[Obsolete] protected override byte getTTL() => throw Unsupported();
		protected override void setTimeToLive(int ttl) => throw Unsupported();
		protected override int getTimeToLive() => throw Unsupported();
		protected override void join(InetAddress inetaddr) => throw Unsupported();
		protected override void leave(InetAddress inetaddr) => throw Unsupported();
		protected override void joinGroup(SocketAddress mcastaddr, NetworkInterface netIf) => throw Unsupported();
		protected override void leaveGroup(SocketAddress mcastaddr, NetworkInterface netIf) => throw Unsupported();
		public override void setOption(int optID, object value) => throw Unsupported();
		public override object getOption(int optID) => throw Unsupported();
	}

	private class EpoxySslSocketFactory : SSLSocketFactory
	{
		public override Socket createSocket(InetAddress host, int port) => new EpoxySslSocket(host, port);
		public override Socket createSocket(string host, int port) => createSocket(InetAddress.getByName(host), port);
		public override Socket createSocket(InetAddress address, int port, InetAddress localAddress, int localPort) => createSocket(address, port);
		public override Socket createSocket(string host, int port, InetAddress localHost, int localPort) => createSocket(host, port);
		public override Socket createSocket(Socket existing, string host, int port, bool autoClose)
		{
			// close connection since we cant upgrade
			if (autoClose) existing.close();
			return createSocket(host, port);
		}

		public override string[] getDefaultCipherSuites() => ["TLS_AES_128_GCM_SHA256"];
		public override string[] getSupportedCipherSuites() => getDefaultCipherSuites();
	}

	// peerCertificates is the chain epoxy verified the server with, leaf first.
	private class EpoxySslSession(string host, int port, Certificate[] peerCertificates) : SSLSession
	{
		private readonly byte[] id = Guid.NewGuid().ToByteArray();
		private readonly long created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		private readonly Dictionary<string, object> values = [];

		public byte[] getId() => id;
		public long getCreationTime() => created;
		public long getLastAccessedTime() => created;

		public void putValue(string name, object value) => values[name] = value;
		public object getValue(string name) => values.TryGetValue(name, out var v) ? v : null;
		public void removeValue(string name) => values.Remove(name);
		public string[] getValueNames() => [.. values.Keys];

		public int getPacketBufferSize() => 17 * 1024;
		public int getApplicationBufferSize() => 16 * 1024;

		public string getProtocol() => "TLSv1.3";
		public string getCipherSuite() => "TLS_AES_128_GCM_SHA256";
		public string getPeerHost() => host;
		public int getPeerPort() => port;

		public bool isValid() => true;
		public void invalidate() { }

		public SSLSessionContext getSessionContext() => null;
		// HostnameVerifiers (okhttp's, HttpsURLConnection's) check the host against these
		public Certificate[] getPeerCertificates() => peerCertificates.Length > 0
			? (Certificate[])peerCertificates.Clone()
			: throw new SSLPeerUnverifiedException("Certificate chain unavailable.");
		public X509Certificate[] getPeerCertificateChain() => throw new SSLPeerUnverifiedException("Certificate chain unavailable.");
		public Principal getPeerPrincipal() => peerCertificates.Length > 0
			? ((java.security.cert.X509Certificate)peerCertificates[0]).getSubjectX500Principal()
			: throw new SSLPeerUnverifiedException("Peer principal unavailable.");
		public Certificate[] getLocalCertificates() => null;
		public Principal getLocalPrincipal() => null;
	}

	private class EpoxySslSocket : SSLSocket
	{
		private WebSocketStream Socket;
		private EpoxySslSession Session;

		private void Connect(InetAddress hostAddr, int port)
		{
			var host = IkvmNetworkState.ResolveHost(hostAddr);

			ClientWebSocket socket = new();
			socket.ConnectAsync(new($"ws://wisp-tls/{host}:{port}"), new()).Wait();

			Socket = new(socket);
			Session = new(host, port, ReadPeerCertificates(Socket));
		}

		// A wisp-tls stream starts with the certificate chain epoxy verified (EpxTcpWs in frontend/src/net.ts): a
		// big-endian length, then the certificates' DER back to back, which is what generateCertificates reads.
		private static Certificate[] ReadPeerCertificates(WebSocketStream stream)
		{
			var header = new byte[4];
			stream.ReadExactly(header, 0, header.Length);
			var chain = new byte[System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header)];
			if (chain.Length == 0)
				return [];
			stream.ReadExactly(chain, 0, chain.Length);

			var certs = CertificateFactory.getInstance("X.509").generateCertificates(new java.io.ByteArrayInputStream(chain));
			return (Certificate[])certs.toArray(new Certificate[0]);
		}

		internal EpoxySslSocket(InetAddress host, int port) => Connect(host, port);
		public override void connect(SocketAddress endpoint)
		{
			var isa = (InetSocketAddress)endpoint;
			Connect(isa.getAddress(), isa.getPort());
		}

        public override void close()
        {
			Socket.Close();
        }

		public override java.io.InputStream getInputStream() => new EpoxyInputStream(Socket);
		public override java.io.OutputStream getOutputStream() => new EpoxyOutputStream(Socket);

        public override SSLSession getSession() => Session;

		public override void addHandshakeCompletedListener(HandshakeCompletedListener listener) => listener.handshakeCompleted(new(this, Session));
		public override void removeHandshakeCompletedListener(HandshakeCompletedListener listener) { }
		public override void startHandshake() { }

        public override bool getEnableSessionCreation() => true;
        public override void setEnableSessionCreation(bool flag) {}

		public override bool getUseClientMode() => true;
        public override void setUseClientMode(bool mode) {}

        public override bool getWantClientAuth() => false;
        public override void setWantClientAuth(bool want) {}
        public override bool getNeedClientAuth() => false;
        public override void setNeedClientAuth(bool need) {}

        public override string[] getSupportedCipherSuites() => ["TLS_AES_128_GCM_SHA256"];
        public override string[] getEnabledCipherSuites() => getSupportedCipherSuites();
        public override void setEnabledCipherSuites(string[] suites) {}

        public override string[] getSupportedProtocols() => ["TLSv1.3"];
        public override string[] getEnabledProtocols() => getSupportedProtocols();
        public override void setEnabledProtocols(string[] protocols) {}
	}
}

// An asynchronous connection for Java callers, using JDK types for callbacks. The callbacks run on whichever
// thread finished the I/O; callers can dispatch onto their own event loop.
public sealed class IkvmWebSocketConnection
{
	private readonly ClientWebSocket Socket = new();
	private readonly CancellationTokenSource Life = new();
	private readonly java.util.function.Consumer OnData;
	private readonly java.util.function.Consumer OnClosed;
	private WebSocketStream Stream;

	// ClientWebSocket forbids concurrent SendAsync, so outbound writes funnel through a single pump.
	private readonly object SendGate = new();
	private readonly Queue<byte[]> SendQueue = new();
	private bool Sending;

	private IkvmWebSocketConnection(java.util.function.Consumer onData, java.util.function.Consumer onClosed)
	{
		OnData = onData;
		OnClosed = onClosed;
	}

	// Dials remote through the wisp proxy. onOpen gets null once connected, or why it couldn't; after StartReceiving,
	// onData gets each inbound chunk as a byte[], and onClosed null at EOF or the error that ended the connection.
	public static IkvmWebSocketConnection Connect(SocketAddress remote, int timeoutMillis, java.util.function.Consumer onOpen, java.util.function.Consumer onData, java.util.function.Consumer onClosed)
	{
		var (host, port) = IkvmNetworkState.ResolveTarget(remote);
		var uri = new Uri($"ws://wisp-tcp/{host}:{port}");

		var connection = new IkvmWebSocketConnection(onData, onClosed);
		var cts = new CancellationTokenSource();
		if (timeoutMillis > 0)
			cts.CancelAfter(timeoutMillis);

		connection.Socket.ConnectAsync(uri, cts.Token).ContinueWith(t =>
		{
			cts.Dispose();
			if (t.IsFaulted || t.IsCanceled)
			{
				connection.Abort();
				var reason = t.IsCanceled ? "timed out" : t.Exception?.GetBaseException().Message;
				onOpen.accept($"wisp websocket connect to {uri} failed: {reason}");
				return;
			}

			connection.Stream = new WebSocketStream(connection.Socket);
			onOpen.accept(null);
		}, TaskScheduler.Default);

		return connection;
	}

	public void StartReceiving() => _ = ReceiveLoopAsync();

	private async Task ReceiveLoopAsync()
	{
		var buf = new byte[16384];
		try
		{
			while (!Life.IsCancellationRequested)
			{
				int n = await Stream.ReadAsync(buf, 0, buf.Length, Life.Token).ConfigureAwait(false);
				if (n <= 0)
				{
					OnClosed.accept(null);
					return;
				}

				OnData.accept(buf.AsSpan(0, n).ToArray());
			}
		}
		catch (OperationCanceledException) { /* closing */ }
		catch (Exception e) { OnClosed.accept(e.Message); }
	}

	public void Send(byte[] bytes)
	{
		lock (SendGate)
		{
			SendQueue.Enqueue(bytes);
			if (Sending)
				return;
			Sending = true;
		}
		_ = SendPumpAsync();
	}

	private async Task SendPumpAsync()
	{
		try
		{
			while (true)
			{
				byte[] next;
				lock (SendGate)
				{
					if (SendQueue.Count == 0)
					{
						Sending = false;
						return;
					}
					next = SendQueue.Dequeue();
				}

				await Stream.WriteAsync(next, 0, next.Length, Life.Token).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException)
		{
			lock (SendGate) { Sending = false; }
		}
		catch (Exception e)
		{
			lock (SendGate) { Sending = false; }
			OnClosed.accept(e.Message);
		}
	}

	// Tears the socket down immediately with no close handshake, so it never blocks the event loop. Also ends a
	// connect still in flight, whose onOpen then reports the failure.
	public void Abort()
	{
		try { Life.Cancel(); } catch { }
		try { Socket.Abort(); } catch { }
		try { Socket.Dispose(); } catch { }
	}
}
