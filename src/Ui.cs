using System;
using System.IO;
using System.Text;

namespace PPGModEnabler
{
    /// <summary>Console helpers. Everything written to the console is also kept for the log file.</summary>
    internal static class Ui
    {
        private static readonly StringBuilder transcript = new StringBuilder();
        private static TextWriter realOut;

        public static bool NoPause;

        public static void Init()
        {
            realOut = Console.Out;
            Console.SetOut(new TeeWriter(realOut, transcript));
        }

        public static string Transcript => transcript.ToString();

        public static void Line(string text = "") => Console.WriteLine(text);
        public static void Title(string text) => Colored(ConsoleColor.Cyan, text);
        public static void Good(string text) => Colored(ConsoleColor.Green, text);
        public static void Warn(string text) => Colored(ConsoleColor.Yellow, text);
        public static void Bad(string text) => Colored(ConsoleColor.Red, text);
        public static void Dim(string text) => Colored(ConsoleColor.DarkGray, text);

        public static void Colored(ConsoleColor color, string text)
        {
            ConsoleColor old = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ForegroundColor = old;
        }

        /// <summary>Reads one line. Returns null when there is no more input (for example piped input ran out).</summary>
        public static string Ask(string prompt)
        {
            Console.Write(prompt);
            string answer = Console.In.ReadLine();
            if (answer == null)
            {
                Console.WriteLine();
                return null;
            }
            transcript.AppendLine(answer);
            return answer.Trim();
        }

        public static bool YesNo(string question, bool defaultYes)
        {
            while (true)
            {
                string answer = Ask(question + (defaultYes ? " [Y/n] " : " [y/N] "));
                if (string.IsNullOrEmpty(answer))
                    return defaultYes;
                switch (answer.ToLowerInvariant())
                {
                    case "y":
                    case "yes":
                        return true;
                    case "n":
                    case "no":
                        return false;
                }
                Warn("Please type y or n.");
            }
        }

        public static void Pause()
        {
            if (NoPause || Console.IsInputRedirected)
                return;
            Console.Write("\nPress Enter to close this window.");
            Console.In.ReadLine();
        }

        private sealed class TeeWriter : TextWriter
        {
            private readonly TextWriter console;
            private readonly StringBuilder copy;

            public TeeWriter(TextWriter console, StringBuilder copy)
            {
                this.console = console;
                this.copy = copy;
            }

            public override Encoding Encoding => console.Encoding;

            public override void Write(char value)
            {
                console.Write(value);
                copy.Append(value);
            }

            public override void Write(string value)
            {
                console.Write(value);
                copy.Append(value);
            }

            public override void WriteLine(string value)
            {
                console.WriteLine(value);
                copy.AppendLine(value);
            }

            public override void Flush() => console.Flush();
        }
    }
}
