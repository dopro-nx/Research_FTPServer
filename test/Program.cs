using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleFtpServer
{
    internal static class Program
    {
        // ====== 설정 ======
        private static readonly FtpServerOptions Options = new()
        {
            ControlPort = 22,

            PassivePortMin = 50000,
            PassivePortMax = 50100,

            RootDirectory = $@"{Environment.GetEnvironmentVariable("SystemDrive")}\FTPImage",

            Username = "admin",
            Password = "1234",

            PassiveAdvertiseAddress = null // null이면 자동으로 로컬IP 중 하나 선택
        };

        private static async Task Main()
        {
            Directory.CreateDirectory(Options.RootDirectory);

            Console.WriteLine("=== Simple FTP Server (PASV/EPSV + PORT + STOR) ===");
            Console.WriteLine($"Root: {Options.RootDirectory}");
            Console.WriteLine($"Control Port: {Options.ControlPort}");
            Console.WriteLine($"PASV Ports: {Options.PassivePortMin}-{Options.PassivePortMax}");
            Console.WriteLine($"User: {Options.Username}");
            Console.WriteLine();

            var server = new FtpServer(Options);
            await server.StartAsync(CancellationToken.None);
        }
    }

    internal sealed class FtpServerOptions
    {
        public int ControlPort { get; set; }
        public int PassivePortMin { get; set; }
        public int PassivePortMax { get; set; }
        public string RootDirectory { get; set; } = "";
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public IPAddress? PassiveAdvertiseAddress { get; set; } = null;
    }

    internal sealed class FtpServer
    {
        private readonly FtpServerOptions _opt;
        private readonly TcpListener _listener;
        private int _nextPassivePort;

        public FtpServer(FtpServerOptions opt)
        {
            _opt = opt;
            _listener = new TcpListener(IPAddress.Any, _opt.ControlPort);
            _nextPassivePort = _opt.PassivePortMin;
        }

        public async Task StartAsync(CancellationToken ct)
        {
            _listener.Start();
            Console.WriteLine($"Listening on 0.0.0.0:{_opt.ControlPort} ...");

            while (!ct.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleClientAsync(client, ct));
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
            Console.WriteLine($"[+] Connected: {remote}");

            using (client)
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true))
            using (var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true })
            {
                var session = new FtpSession(_opt, remote);
                session.SetRoot(_opt.RootDirectory);

                await writer.WriteLineAsync("220 SimpleFtpServer Ready");

                try
                {
                    while (!ct.IsCancellationRequested && client.Connected)
                    {
                        var line = await reader.ReadLineAsync();
                        if (line == null) break;
                        if (line.Length == 0) continue;

                        Console.WriteLine($"[{remote}] C> {line}");

                        var (cmd, arg) = ParseCommand(line);

                        switch (cmd)
                        {
                            case "USER":
                                session.User = arg ?? "";
                                await writer.WriteLineAsync("331 Username ok, need password");
                                break;

                            case "PASS":
                                session.IsAuthed = (session.User == _opt.Username && (arg ?? "") == _opt.Password);
                                await writer.WriteLineAsync(session.IsAuthed ? "230 Login successful" : "530 Login incorrect");
                                break;

                            case "SYST":
                                await writer.WriteLineAsync("215 UNIX Type: L8");
                                break;

                            case "FEAT":
                                await writer.WriteLineAsync("211-Features");
                                await writer.WriteLineAsync(" PASV");
                                await writer.WriteLineAsync(" EPSV");
                                await writer.WriteLineAsync(" UTF8");
                                await writer.WriteLineAsync("211 End");
                                break;

                            case "OPTS":
                                await writer.WriteLineAsync("200 OPTS ok");
                                break;

                            case "PWD":
                                await writer.WriteLineAsync($"257 \"{session.CurrentVirtualPath}\" is current directory");
                                break;

                            case "CWD":
                                if (!EnsureLogin(session, writer)) break;
                                await HandleCwd(session, writer, arg);
                                break;

                            case "MKD":
                                if (!EnsureLogin(session, writer)) break;
                                await HandleMkd(session, writer, arg);
                                break;

                            case "TYPE":
                                session.TransferType = (arg ?? "I").Trim().ToUpperInvariant();
                                await writer.WriteLineAsync("200 Type set");
                                break;

                            // ===== Passive =====
                            case "PASV":
                                if (!EnsureLogin(session, writer)) break;
                                await HandlePasv(session, writer);
                                break;

                            case "EPSV":
                                if (!EnsureLogin(session, writer)) break;
                                await HandleEpsv(session, writer);
                                break;

                            // ===== Active =====
                            case "PORT":
                                if (!EnsureLogin(session, writer)) break;
                                await HandlePort(session, writer, arg);
                                break;

                            case "STOR":
                                if (!EnsureLogin(session, writer)) break;
                                await HandleStor(session, writer, arg, ct);
                                break;

                            case "NOOP":
                                await writer.WriteLineAsync("200 NOOP ok");
                                break;

                            case "QUIT":
                                await writer.WriteLineAsync("221 Bye");
                                return;

                            case "CLNT":
                            case "SITE":
                            case "PBSZ":
                            case "PROT":
                                await writer.WriteLineAsync("200 OK");
                                break;

                            default:
                                await writer.WriteLineAsync("502 Command not implemented");
                                break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[!] {remote} Error: {ex.Message}");
                }
                finally
                {
                    session.ClosePassiveListener();
                    Console.WriteLine($"[-] Disconnected: {remote}");
                }
            }
        }

        private static (string cmd, string? arg) ParseCommand(string line)
        {
            var idx = line.IndexOf(' ');
            if (idx < 0) return (line.Trim().ToUpperInvariant(), null);
            var cmd = line.Substring(0, idx).Trim().ToUpperInvariant();
            var arg = line.Substring(idx + 1).Trim();
            return (cmd, arg);
        }

        private static bool EnsureLogin(FtpSession session, StreamWriter writer)
        {
            if (session.IsAuthed) return true;
            writer.WriteLine("530 Not logged in");
            return false;
        }

        private async Task HandleCwd(FtpSession session, StreamWriter writer, string? arg)
        {
            if (string.IsNullOrWhiteSpace(arg))
            {
                await writer.WriteLineAsync("550 Failed to change directory");
                return;
            }

            var ok = session.TryChangeDirectory(arg.Trim());
            await writer.WriteLineAsync(ok ? "250 Directory changed" : "550 Failed to change directory");
        }

        private async Task HandleMkd(FtpSession session, StreamWriter writer, string? arg)
        {
            if (string.IsNullOrWhiteSpace(arg))
            {
                await writer.WriteLineAsync("550 MKD failed");
                return;
            }

            var (ok, createdVirtPath) = session.TryMakeDirectory(arg.Trim());
            await writer.WriteLineAsync(ok
                ? $"257 \"{createdVirtPath}\" directory created"
                : "550 MKD failed");
        }

        private int AllocatePassivePort()
        {
            // 라운드로빈
            int port = Interlocked.Increment(ref _nextPassivePort);
            if (port > _opt.PassivePortMax)
            {
                Interlocked.Exchange(ref _nextPassivePort, _opt.PassivePortMin);
                port = _opt.PassivePortMin;
            }
            return port;
        }

        private async Task HandlePasv(FtpSession session, StreamWriter writer)
        {
            // Passive를 쓰면 Active 정보는 무시(클라이언트가 모드를 바꾼 것)
            session.ActiveDataEndPoint = null;

            session.ClosePassiveListener();

            int port = AllocatePassivePort();

            var pasvListener = new TcpListener(IPAddress.Any, port);
            pasvListener.Start();
            session.PassiveListener = pasvListener;

            var advertiseIp = _opt.PassiveAdvertiseAddress ?? GuessLocalIpForClient(session.RemoteEndPoint);
            if (advertiseIp == null) advertiseIp = IPAddress.Loopback;

            var ipBytes = advertiseIp.GetAddressBytes();
            var p1 = port / 256;
            var p2 = port % 256;

            await writer.WriteLineAsync($"227 Entering Passive Mode ({ipBytes[0]},{ipBytes[1]},{ipBytes[2]},{ipBytes[3]},{p1},{p2})");
        }

        private async Task HandleEpsv(FtpSession session, StreamWriter writer)
        {
            // Passive를 쓰면 Active 정보는 무시
            session.ActiveDataEndPoint = null;

            session.ClosePassiveListener();

            int port = AllocatePassivePort();

            var epsvListener = new TcpListener(IPAddress.Any, port);
            epsvListener.Start();
            session.PassiveListener = epsvListener;

            // 229 Entering Extended Passive Mode (|||port|)
            await writer.WriteLineAsync($"229 Entering Extended Passive Mode (|||{port}|)");
        }

        private async Task HandlePort(FtpSession session, StreamWriter writer, string? arg)
        {
            // Active 모드로 전환되면 기존 Passive listener는 닫기
            session.ClosePassiveListener();

            if (string.IsNullOrWhiteSpace(arg))
            {
                await writer.WriteLineAsync("501 Missing PORT argument");
                return;
            }

            // PORT h1,h2,h3,h4,p1,p2
            // 예: 192,168,0,30,224,12  =>  port = 224*256 + 12
            if (!TryParsePortArgument(arg.Trim(), out var endPoint))
            {
                await writer.WriteLineAsync("501 Invalid PORT argument");
                return;
            }

            session.ActiveDataEndPoint = endPoint;
            await writer.WriteLineAsync("200 PORT command successful");
        }

        private static bool TryParsePortArgument(string arg, out IPEndPoint endPoint)
        {
            endPoint = new IPEndPoint(IPAddress.Loopback, 0);

            var parts = arg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 6) return false;

            if (!byte.TryParse(parts[0], out var h1)) return false;
            if (!byte.TryParse(parts[1], out var h2)) return false;
            if (!byte.TryParse(parts[2], out var h3)) return false;
            if (!byte.TryParse(parts[3], out var h4)) return false;
            if (!byte.TryParse(parts[4], out var p1)) return false;
            if (!byte.TryParse(parts[5], out var p2)) return false;

            var ip = new IPAddress(new byte[] { h1, h2, h3, h4 });
            int port = (p1 * 256) + p2;

            if (port < 1 || port > 65535) return false;

            endPoint = new IPEndPoint(ip, port);
            return true;
        }

        private async Task HandleStor(FtpSession session, StreamWriter writer, string? arg, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(arg))
            {
                await writer.WriteLineAsync("501 Missing filename");
                return;
            }

            // 파일 경로 계산 (루트 아래로만)
            var targetPath = session.ResolveVirtualToPhysicalFilePath(arg.Trim());
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            var tmpPath = targetPath + ".tmp";

            await writer.WriteLineAsync("150 Opening data connection for STOR");

            TcpClient? dataClient = null;
            try
            {
                // 1) Passive 우선: PASV/EPSV를 썼다면 여기로 옴
                if (session.PassiveListener != null)
                {
                    dataClient = await session.PassiveListener.AcceptTcpClientAsync(ct);
                }
                // 2) Active: PORT로 endpoint 지정되면 서버가 클라이언트로 connect
                else if (session.ActiveDataEndPoint != null)
                {
                    dataClient = new TcpClient();
                    await dataClient.ConnectAsync(session.ActiveDataEndPoint.Address, session.ActiveDataEndPoint.Port, ct);
                }
                else
                {
                    await writer.WriteLineAsync("425 Use PASV/EPSV or PORT first");
                    return;
                }

                using (dataClient)
                using (var dataStream = dataClient.GetStream())
                using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await dataStream.CopyToAsync(fs, 81920, ct);
                }

                if (File.Exists(targetPath)) File.Delete(targetPath);
                File.Move(tmpPath, targetPath);

                Console.WriteLine($"[{session.RemoteEndPoint}] Uploaded: {targetPath}");

                await writer.WriteLineAsync("226 Transfer complete");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] STOR failed ({session.RemoteEndPoint}): {ex.Message}");
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
                await writer.WriteLineAsync("426 Connection closed; transfer aborted");
            }
            finally
            {
                // Passive는 1회용으로 정리
                session.ClosePassiveListener();

                // Active endpoint는 클라이언트가 PORT를 다시 보내기 전까지 유지해도 되지만,
                // 클라이언트 구현에 따라 매번 PORT를 다시 주므로 남겨둬도/지워도 큰 차이 없음.
                // 여기서는 안전하게 유지(연속 STOR 대비)
                // session.ActiveDataEndPoint = null;
            }
        }

        private static IPAddress? GuessLocalIpForClient(string remoteEndPoint)
        {
            if (!TryParseRemoteIp(remoteEndPoint, out var remoteIp))
                return null;

            try
            {
                using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                s.Connect(remoteIp, 1);
                if (s.LocalEndPoint is IPEndPoint lep)
                    return lep.Address;
            }
            catch { }
            return null;
        }

        private static bool TryParseRemoteIp(string remoteEndPoint, out IPAddress ip)
        {
            ip = IPAddress.None;
            var host = remoteEndPoint.Split(':').FirstOrDefault();
            return host != null && IPAddress.TryParse(host, out ip);
        }
    }

    internal sealed class FtpSession
    {
        private readonly FtpServerOptions _opt;

        public FtpSession(FtpServerOptions opt, string remoteEndPoint)
        {
            _opt = opt;
            RemoteEndPoint = remoteEndPoint;
        }

        public string RemoteEndPoint { get; }
        public string User { get; set; } = "";
        public bool IsAuthed { get; set; }
        public string TransferType { get; set; } = "I"; // I=binary

        // Passive
        public TcpListener? PassiveListener { get; set; }

        // Active (PORT로 지정된 데이터 접속 목적지)
        public IPEndPoint? ActiveDataEndPoint { get; set; }

        private string _root = "";
        private string _cwdVirtual = "/";

        public string CurrentVirtualPath => _cwdVirtual;

        public void SetRoot(string root)
        {
            _root = Path.GetFullPath(root);
            _cwdVirtual = "/";
        }

        public void ClosePassiveListener()
        {
            try { PassiveListener?.Stop(); } catch { }
            PassiveListener = null;
        }

        public bool TryChangeDirectory(string arg)
        {
            var virt = NormalizeVirtualPath(arg);
            var phys = ResolveVirtualToPhysicalDirectory(virt);
            if (!Directory.Exists(phys)) return false;

            _cwdVirtual = virt;
            return true;
        }

        public (bool ok, string createdVirtPath) TryMakeDirectory(string arg)
        {
            var virt = NormalizeVirtualPath(arg);
            var phys = ResolveVirtualToPhysicalDirectory(virt);

            try
            {
                Directory.CreateDirectory(phys);
                return (true, virt);
            }
            catch
            {
                return (false, virt);
            }
        }

        public string ResolveVirtualToPhysicalFilePath(string filenameOrPath)
        {
            var virtPath = NormalizeVirtualPath(filenameOrPath);

            var combined = Path.Combine(_root, virtPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            var full = Path.GetFullPath(combined);

            if (!full.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Invalid path (root escape)");

            return full;
        }

        private string ResolveVirtualToPhysicalDirectory(string virtDir)
        {
            var combined = Path.Combine(_root, virtDir.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            var full = Path.GetFullPath(combined);

            if (!full.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Invalid path (root escape)");

            return full;
        }

        private string NormalizeVirtualPath(string input)
        {
            input = input.Trim();
            if (string.IsNullOrEmpty(input)) return _cwdVirtual;

            if (input.StartsWith("/"))
                return CollapseDots(input);

            if (_cwdVirtual.EndsWith("/"))
                return CollapseDots(_cwdVirtual + input);

            return CollapseDots(_cwdVirtual + "/" + input);
        }

        private static string CollapseDots(string path)
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var stack = new System.Collections.Generic.Stack<string>();

            foreach (var p in parts)
            {
                if (p == ".") continue;
                if (p == "..")
                {
                    if (stack.Count > 0) stack.Pop();
                    continue;
                }
                stack.Push(p);
            }

            var norm = "/" + string.Join("/", stack.Reverse());
            if (path.EndsWith("/") && !norm.EndsWith("/")) norm += "/";
            return norm == "" ? "/" : norm;
        }
    }
}