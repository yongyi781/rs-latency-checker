using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace RSLatencyChecker
{
    public class BisectionResult
    {
        public string Server { get; set; }
        public string World { get; set; }
        public int Min { get; set; }
        public int Max { get; set; }
    }

    static class Program
    {
        static readonly byte[] sendData = { 14 };
        static readonly byte[] receiveBuffer = new byte[9];
        static int port = 43594;

        static void Main(string[] args)
        {
            int trials = 10;
            if (HasCommandLineSwitch(args, "https") || HasCommandLineSwitch(args, "443"))
                port = 443;

            if (HasCommandLineSwitch(args, "help") || HasCommandLineSwitch(args, "h"))
            {
                Console.WriteLine("Usage: RSLatencyChecker.exe [-m[easureticks]] OR -s[ingle]] [-https|443] [<number of trials>]");
                return;
            }
            if (HasCommandLineSwitch(args, "measureticks") || HasCommandLineSwitch(args, "m"))
            {
                if (args.Length > 1 && !int.TryParse(args[1], out trials) || trials < 0)
                    trials = 1000;
                while (true)
                {
                    Console.Write("Enter a world: ");
                    MeasureTickLength(Console.ReadLine(), trials, verbose: true);
                }
            }
            else if (HasCommandLineSwitch(args, "single") || HasCommandLineSwitch(args, "s"))
            {
                while (true)
                {
                    Console.Write("Enter a world: ");
                    BisectionAsync(Console.ReadLine(), string.Empty, trials, true).Wait();
                }
            }

            if (args.Length > 0 && !int.TryParse(args[args.Length - 1], out trials) || trials < 0)
                    trials = 10;

            const string FileName = "worlds.txt";
            if (!File.Exists(FileName))
            {
                Console.WriteLine("Please create a file called 'worlds.txt' with a list of worlds you want to test, and try again.");
                Console.ReadKey(true);
                return;
            }
            var worlds = (from line in File.ReadAllLines(FileName) select line.Split(null)).ToArray();
            Console.WriteLine($"Measuring latency from {worlds.Length} servers ({trials} trials) on port {port}...");
            var sw = Stopwatch.StartNew();
            var task = Task.WhenAll((from world in worlds
                                     select BisectionAsync(world[0], world[1], trials)).ToArray());
            var results = task.Result.OrderBy(item => item.Min).ToList();
            sw.Stop();
            Console.WriteLine("{0,12}{1,6}{2,8}{3,8}{4,8}", "Server", "World", "Min", "Max", "Range");
            foreach (var item in results)
                Console.WriteLine("{0,12}{1,6}{2,6}ms{3,6}ms{4,6}ms", item.Server, item.World, item.Min, item.Max, item.Max - item.Min);
            Console.Write("Total elapsed time: {0}s\nPress Enter to continue . . . ", sw.Elapsed.TotalSeconds);
            Console.ReadLine();
        }

        static string GetFullWorldString(string worldStr)
        {
            return int.TryParse(worldStr, out _) ? "world" + worldStr : worldStr;
        }

        static bool HasCommandLineSwitch(string[] args, string arg)
        {
            return args.Any(s => s.Length >= 1 && (s[0] == '/' || s[0] == '-') && s.Substring(1).Equals(arg, StringComparison.OrdinalIgnoreCase));
        }

        static void MeasureTickLength(string world, int trials = 1000, bool verbose = false)
        {
            string host = "world" + world + ".runescape.com";
            try
            {
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    socket.Connect(host, port);
                    // Align ourselves on a tick first
                    socket.Send(sendData);
                    socket.Receive(receiveBuffer);
                    var sw = new Stopwatch();
                    double min = double.PositiveInfinity, max = 0.0;
                    double lastElapsed = 0.0;
                    sw.Start();
                    for (int i = 1; i <= trials; i++)
                    {
                        socket.Send(sendData);
                        socket.Receive(receiveBuffer);
                        double elapsed = sw.Elapsed.TotalMilliseconds;
                        double tickTime = elapsed - lastElapsed;
                        if (tickTime > 900.0)
                        {
                            // Bad result
                            if (verbose)
                                Console.WriteLine("Trial {0} of {1}: Bad result; aborted", i, trials);
                            break;
                        }
                        if (min > tickTime)
                            min = tickTime;
                        if (max < tickTime)
                            max = tickTime;
                        lastElapsed = elapsed;
                        if (verbose)
                            Console.WriteLine("Trial {0} of {1}: time={2:0.0000}ms avg={3:0.000}", i, trials, tickTime, elapsed / i);
                    }
                    sw.Stop();
                    if (verbose)
                        Console.WriteLine("Summary: min={0:0.0000}ms max={1:0.0000}ms range={2:0.0000}ms\n", min, max, max - min);
                }
            }
            catch (SocketException)
            {
                Console.WriteLine("An error occurred while connecting to {0}. Try again.\n", host);
            }
        }

        static Task ConnectTaskAsync(this Socket socket, string host, int port)
        {
            return Task.Factory.FromAsync(socket.BeginConnect, socket.EndConnect, host, port, null);
        }

        static Task ReceiveDataAsync(this Socket socket)
        {
            return Task.Factory.FromAsync<int>(socket.BeginReceive(receiveBuffer, 0, receiveBuffer.Length, SocketFlags.None, null, null), socket.EndReceive);
        }

        static async Task<BisectionResult> BisectionAsync(string world, string server, int trials, bool verbose = false)
        {
            const int Timeout = 10000;

            string host = GetFullWorldString(world) + ".runescape.com";
            try
            {
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    var connectTask = socket.ConnectTaskAsync(host, port);
                    // The following simulates an async timeout after Timeout ms.
                    if (await Task.WhenAny(connectTask, Task.Delay(Timeout)) != connectTask)
                    {
                        Console.WriteLine($"Failed to connect to world {world}");
                        return new BisectionResult { World = world, Min = -1, Max = -1 };
                    }

                    // Align ourselves on a tick first
                    socket.Send(sendData);
                    await socket.ReceiveDataAsync();
                    int low = 0, high = 600, min = int.MaxValue, max = 0;
                    var sw = new Stopwatch();
                    for (int i = 1; i <= trials; i++)
                    {
                        int mid = (low + high) / 2;
                        await Task.Delay(mid);
                        socket.Send(sendData);
                        sw.Start();
                        await socket.ReceiveDataAsync();
                        sw.Stop();
                        int elapsed = (int)sw.ElapsedMilliseconds;
                        if (elapsed < 600)
                            low = mid;
                        else
                            high = mid;
                        if (min > elapsed)
                            min = elapsed;
                        if (max < elapsed)
                            max = elapsed;
                        if (verbose)
                            Console.WriteLine($"World {world}, Trial {i} of {trials}: time={elapsed}ms min={min}ms max={max}ms");
                        sw.Reset();
                    }
                    if (verbose)
                        Console.WriteLine("Summary for world {3}: min={0}ms max={1}ms range={2}ms\n", min, max, max - min, world);
                    return new BisectionResult { World = world, Server = server, Min = min, Max = max };
                }
            }
            catch (SocketException)
            {
                Console.WriteLine("An error occurred while connecting to {0}. Try again.\n", host);
                return new BisectionResult { World = world, Server = server, Min = -1, Max = -1 };
            }
        }
    }
}
