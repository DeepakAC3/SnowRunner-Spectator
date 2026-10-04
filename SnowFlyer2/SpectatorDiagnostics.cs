using System;
using System.IO;
using System.Text;

namespace SnowFlyer2
{
    partial class Program
    {
        private static void InitializeSpectatorDiagnostics()
        {
            string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            try
            {
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, "spectator-" +
                    DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
                var file = new StreamWriter(new FileStream(path, FileMode.Create,
                    FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
                    { AutoFlush = true };
                Console.SetOut(TextWriter.Synchronized(new SpectatorDiagnosticWriter(Console.Out, file)));
                Console.WriteLine("Spectator runtime log: " + path);
            }
            catch (IOException ex)
            {
                Console.WriteLine("Could not create spectator log: " + ex.Message);
            }
        }
    }

    internal sealed class SpectatorDiagnosticWriter : TextWriter
    {
        private readonly TextWriter _console;
        private readonly StreamWriter _file;
        public override Encoding Encoding => _console.Encoding;

        internal SpectatorDiagnosticWriter(TextWriter console, StreamWriter file)
        {
            _console = console;
            _file = file;
        }

        public override void Write(char value)
        {
            _console.Write(value);
            _file.Write(value);
        }

        public override void Write(string value)
        {
            _console.Write(value);
            _file.Write(value);
        }

        public override void WriteLine(string value)
        {
            _console.WriteLine(value);
            _file.WriteLine(DateTime.UtcNow.ToString("O") + " " + value);
        }

        public override void Flush()
        {
            _console.Flush();
            _file.Flush();
        }
    }
}
