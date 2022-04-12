using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SPBuild
{
    internal class Program
    {
        private static Regex Macher = new(@"^(.*)\((.+)\)\s:\s(.+)\s(\d+):(.*)$");
        private static Regex SRegex = new(@"[0-9]+(\ )bytes$");
        private static Regex MainFileMacher = new(@"\s?//\s?MAIN_FILE\s+(.+)");
        private static string Worker = Environment.CurrentDirectory;

        static void Main(string[] args)
        {
            Init();

            var startTime = DateTime.Now;

            var spcomp = Path.Combine(Worker, "spcomp.exe");

            try
            {
                var path = Path.Combine(Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location) ?? Worker, "spcomp.exe");
                if (!File.Exists(path))
                    File.WriteAllBytes(path, Properties.Resources.spcomp);

                spcomp = path;
            }
            catch
            {
                Console.WriteLine("error: Failed to init environment.");
                Environment.Exit(1);
            }

            if (args.Length < 3)
            {
                Console.WriteLine("error: Invalid app args.");
                Console.WriteLine("args: <file> <output> <include> [include] ...");
                Environment.Exit(1);
            }

            var resolved = false;

            var input = args[0].Replace("\"", "");

        try_resolve:
            if (!File.Exists(input))
            {
                Console.WriteLine("error: Invalid input file: " + input);
                Console.WriteLine("args: <file> <output> <include> [include] ...");
                Environment.Exit(1);
            }

            // resolve
            if (!resolved)
            {
                try
                {
                    var text = File.ReadAllLines(input);
                    for (var i = 0; i < 64 && i < text.Length; i++)
                    {
                        var match = MainFileMacher.Match(text[i]);
                        if (match.Success && match.Groups.Count == 2)
                        {
                            var main = match.Groups[1].Value;
                            input = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(input), main));
                            //Console.WriteLine($"try resolve: {main} to {input}");
                            resolved = true;
                            goto try_resolve;
                        }
                    }
                }
                catch { }
            }

            var baseDir = Path.GetDirectoryName(input); //+ @"\";

            var output = args[1].Replace("\"", "");
            if (File.Exists(output))
                File.Delete(output);

            var dir = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var includes = new StringBuilder();
            for (var i = 2; i < args.Length; i++)
            {
                var arg = args[i].Replace("\"", "");
                if (Directory.Exists(arg))
                    includes.Append($"-i\"{arg}\" ");
            }

            var psi = new ProcessStartInfo
            {
                FileName = spcomp,
                Arguments = $"\"{input}\" " +
                            $"-O2 " +
                            $"-v2 " +
                            $"-h " +
                            $"-o\"{output}\" " +
                            includes,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8
            };

            ColorText(ConsoleColor.Green, $"    {CON_BOLD}Compiling {CON_RESET}");
            ColorText(ConsoleColor.White, Path.GetFileNameWithoutExtension(input));
            ColorText(ConsoleColor.DarkCyan, $" ({Path.GetDirectoryName(input)}){Environment.NewLine}", true);

            using var p = Process.Start(psi);
            var result = p.StandardOutput.ReadToEnd();
            p.WaitForExit();

            //ColorText(ConsoleColor.DarkCyan, result);

            var endTime = DateTime.Now;

            var success = result.Contains("Code size:");

            var errors = 0;
            var warnings = 0;
            var response = new StringBuilder();
            var iRefs = new List<string>();
            var files = new List<string>();

            foreach (var data in result.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var match = Macher.Match(data);
                if (!match.Success || match.Groups.Count != 6)
                {
                    //ColorText(ConsoleColor.Red, $"Error parse: {data}", true);
                    if (SRegex.IsMatch(data))
                        response.AppendLine(data);
                    else if (data.EndsWith(".inc") && data.StartsWith("Note: ") && CheckInclude(data, baseDir, out var inc))
                        iRefs.Add(inc);
                    else if (data.EndsWith(".sp") && data.StartsWith("Note: ") && CheckFile(data, baseDir, out var sp))
                        files.Add(sp);

                    continue;
                }

                var file = match.Groups[1].Value;
                var line = match.Groups[2].Value;
                var kind = match.Groups[3].Value;
                var code = match.Groups[4].Value;
                var text = match.Groups[5].Value;

                if (kind.Equals("error") || kind.Equals("fatal error"))
                {
                    errors++;
                    ColorText(ConsoleColor.Red, $"{CON_BOLD}{kind}[{code}]{CON_RESET}");
                }
                else if (kind.Equals("warning"))
                {
                    warnings++;
                    ColorText(ConsoleColor.Yellow, $"{CON_BOLD}{kind}{CON_RESET}");
                }

                ColorText(ConsoleColor.White, $"{CON_BOLD}:{CON_RESET}");
                ColorText(ConsoleColor.White, $" {text}", true);

                ColorText(ConsoleColor.Cyan, $"{new string(' ', line.Length)}{CON_BOLD}-->{CON_RESET} ");

                var path = file[2] == ':' ? file : Path.Combine(baseDir, file);
                //if (path.StartsWith("/"))
                //    path = "." + path;
                //else if (!path.StartsWith("./"))
                //    path = "./" + path;

                ColorText(ConsoleColor.DarkGray, path.Replace('/', '\\') + ":" + line, true);
            }

            Console.Write(Environment.NewLine);

            foreach (var inc in iRefs.Distinct().ToList())
            {
                ColorText(ConsoleColor.DarkMagenta, $"{CON_BOLD}including{CON_RESET}");
                ColorText(ConsoleColor.White, $"{CON_BOLD}:{CON_RESET}");
                ColorText(ConsoleColor.DarkCyan, $" {inc}", true);
            }

            Console.Write(Environment.NewLine);

            files = files.Distinct().OrderBy(x => x).ToList();
            files.Add(Path.GetFileName(input));
            foreach (var sp in files)
            {
                ColorText(ConsoleColor.DarkMagenta, $"{CON_BOLD}compiling{CON_RESET}");
                ColorText(ConsoleColor.White, $"{CON_BOLD}:{CON_RESET}");
                ColorText(ConsoleColor.DarkCyan, $" {sp}", true);
            }

            Console.Write(Environment.NewLine);

            if (warnings > 0)
            {
                ColorText(ConsoleColor.Yellow, $"{CON_BOLD}warning{CON_RESET}");
                ColorText(ConsoleColor.White, $"{CON_BOLD}:{CON_RESET}");
                ColorText(ConsoleColor.White, $" generated {warnings} warning", true);
            }

            if (errors > 0)
            {
                ColorText(ConsoleColor.Red, $"{CON_BOLD}errors{CON_RESET}");
                ColorText(ConsoleColor.White, $"{CON_BOLD}:{CON_RESET}");
                ColorText(ConsoleColor.White, $" generated {errors} error", true);
            }
            else if (success)
            {
                ColorText(ConsoleColor.White, response.ToString(), true);

                ColorText(ConsoleColor.Green, Environment.NewLine + "    Finished");
                ColorText(ConsoleColor.White, $" in {((endTime - startTime).Milliseconds * 0.001)} s", true);
            }

            //Console.ReadKey(true);
            Environment.Exit(0);
        }

        private static bool CheckFile(string data, string worker, out string sp)
        {
            sp = string.Empty;

            if (!data.StartsWith("Note: including file: "))
                return false;

            var path = data.Replace("Note: including file: ", "").Trim();
            //var file = Path.GetFullPath(Path.Combine(worker, path));
            //if (File.Exists(file))
            //{
            //    sp = file.Replace('\\', '/');
            //    return true;
            //}

            sp = path.Replace('\\', '/');
            return true;
        }

        private static bool CheckInclude(string data, string worker, out string inc)
        {
            inc = string.Empty;

            if (!data.StartsWith("Note: including file: "))
                return false;

            var path = data.Replace("Note: including file: ", "").Trim().ToLowerInvariant();
            var file = Path.GetFileName(path);
            if (OfficialInclude.Contains(file))
                return false;

            var replace = worker.ToLowerInvariant() + @"\";
            inc = path.Replace(replace, "").Replace('\\', '/');

            return true;
        }

        private static void ColorText(ConsoleColor color, string text, bool newline = false)
        {
            Console.ForegroundColor = color;
            Console.Write(text + (newline ? Environment.NewLine : ""));
        }

        private static void Init()
        {
            Console.Title = "SP Build by Kyle";

            var handle = GetStdHandle(STD_OUTPUT_HANDLE);
            GetConsoleMode(handle, out var mode);
            mode |= ENABLE_VIRTUAL_TERMINAL_PROCESSING;
            SetConsoleMode(handle, mode);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll")]
        private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        [DllImport("kernel32.dll")]
        private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

        private const int STD_OUTPUT_HANDLE = -11;
        private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 4;

        private const string CON_BOLD = "\x1b[1m";
        private const string CON_RESET = "\x1b[0m";

        private static readonly List<string> OfficialInclude = new()
        {
            "admin.inc",
            "adminmenu.inc",
            "adt.inc",
            "adt_array.inc",
            "adt_stack.inc",
            "adt_trie.inc",
            "banning.inc",
            "basecomm.inc",
            "bitbuffer.inc",
            "clientprefs.inc",
            "clients.inc",
            "commandfilters.inc",
            "commandline.inc",
            "console.inc",
            "convars.inc",
            "core.inc",
            "cstrike.inc",
            "datapack.inc",
            "dbi.inc",
            "entity.inc",
            "entity_prop_stocks.inc",
            "events.inc",
            "files.inc",
            "float.inc",
            "functions.inc",
            "geoip.inc",
            "halflife.inc",
            "handles.inc",
            "helpers.inc",
            "keyvalues.inc",
            "lang.inc",
            "logging.inc",
            "mapchooser.inc",
            "menus.inc",
            "nextmap.inc",
            "profiler.inc",
            "protobuf.inc",
            "regex.inc",
            "sdkhooks.inc",
            "sdktools.inc",
            "sdktools_client.inc",
            "sdktools_engine.inc",
            "sdktools_entinput.inc",
            "sdktools_entoutput.inc",
            "sdktools_functions.inc",
            "sdktools_gamerules.inc",
            "sdktools_hooks.inc",
            "sdktools_sound.inc",
            "sdktools_stocks.inc",
            "sdktools_stringtables.inc",
            "sdktools_tempents.inc",
            "sdktools_tempents_stocks.inc",
            "sdktools_trace.inc",
            "sdktools_variant_t.inc",
            "sdktools_voice.inc",
            "sorting.inc",
            "sourcemod.inc",
            "string.inc",
            "testing.inc",
            "textparse.inc",
            "tf2.inc",
            "tf2_stocks.inc",
            "timers.inc",
            "topmenus.inc",
            "usermessages.inc",
            "vector.inc",
            "version.inc",
            "version_auto.inc",
        };
    }
}
