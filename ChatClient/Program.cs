using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ChatClient
{
    class Program
    {
        static async Task Main()
        {
            Console.WriteLine("=== TCP Chat Client ===");
            Console.Write("Server IP (default 127.0.0.1): ");
            string? ip = Console.ReadLine(); if (string.IsNullOrWhiteSpace(ip)) ip = "127.0.0.1";
            Console.Write("Port (default 5000): ");
            string? portStr = Console.ReadLine(); if (!int.TryParse(portStr, out int port)) port = 5000;
            Console.Write("Username: "); string username = Console.ReadLine() ?? "";
            Console.Write("Password: "); string password = Console.ReadLine() ?? "";

            try
            {
                using var tcp = new TcpClient();
                await tcp.ConnectAsync(ip, port);
                using var stream = tcp.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

                var loginReq = new { type = "LOGIN_REQ", payload = new { username, password } };
                await writer.WriteLineAsync(JsonSerializer.Serialize(loginReq));
                string? loginResp = await reader.ReadLineAsync();
                if (loginResp == null) { Console.WriteLine("Server closed connection."); return; }

                var doc = JsonDocument.Parse(loginResp);
                bool ok = doc.RootElement.GetProperty("payload").GetProperty("ok").GetBoolean();
                if (!ok) { string reason = doc.RootElement.GetProperty("payload").GetProperty("reason").GetString() ?? "Unknown"; Console.WriteLine($"Login failed: {reason}"); return; }

                Console.WriteLine($"✅ Logged in as {username}");

                // Heartbeat
                _ = Task.Run(async () => { try { while (true) { var hb = new { type = "HEARTBEAT", payload = new { } }; await writer.WriteLineAsync(JsonSerializer.Serialize(hb)); await Task.Delay(20000); } } catch { } });

                // Receive loop
                _ = Task.Run(async () => { try { while (true) { string? line = await reader.ReadLineAsync(); if (line == null) break; try { var msg = JsonDocument.Parse(line); string type = msg.RootElement.GetProperty("type").GetString() ?? ""; Console.ForegroundColor = ConsoleColor.Cyan; Console.WriteLine($"\n[{DateTime.Now:T}] << {type} >>: {msg.RootElement.GetProperty("payload")}"); Console.ResetColor(); } catch { Console.WriteLine($"Invalid JSON: {line}"); } } } catch (Exception ex) { Console.WriteLine($"Read error: {ex.Message}"); } });

                Console.WriteLine("Commands: /dm <user> <msg>, /multi <u1,u2,...> <msg>, /broadcast <msg>, /exit");
                while (true)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow; Console.Write("> "); Console.ResetColor(); string? input = Console.ReadLine(); if (string.IsNullOrWhiteSpace(input)) continue; if (input.StartsWith("/exit", StringComparison.OrdinalIgnoreCase)) break;
                    string type; object payload;
                    if (input.StartsWith("/dm ")) { var parts = input.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries); if (parts.Length < 3) { Console.WriteLine("Usage: /dm <user> <msg>"); continue; } type = "DM"; payload = new { to = parts[1], msg = parts[2] }; }
                    else if (input.StartsWith("/multi ")) { var parts = input.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries); if (parts.Length < 3) { Console.WriteLine("Usage: /multi <u1,u2,...> <msg>"); continue; } type = "MULTI"; var users = parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries); payload = new { to = users, msg = parts[2] }; }
                    else if (input.StartsWith("/broadcast ")) { string msg2 = input.Substring(11).Trim(); if (msg2.Length == 0) { Console.WriteLine("Usage: /broadcast <msg>"); continue; } type = "BROADCAST"; payload = new { msg = msg2 }; }
                    else { Console.WriteLine("Unknown command."); continue; }
                    var pkt = new { type, payload }; await writer.WriteLineAsync(JsonSerializer.Serialize(pkt));
                }
                Console.WriteLine("Disconnecting...");
            }
            catch (Exception ex) { Console.WriteLine($"Error: {ex.Message}"); }
        }
    }
}