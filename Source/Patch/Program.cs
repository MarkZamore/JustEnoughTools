using BsDiff;

namespace JustEnoughTools.Patch;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 4 || args[0] is not ("create" or "apply"))
        {
            Console.Error.WriteLine("Usage: DeltaPatchTool <create|apply> <old-file> <new-or-patch-file> <output-file>");
            return 2;
        }

        try
        {
            var source = Path.GetFullPath(args[1]);
            var input = Path.GetFullPath(args[2]);
            var destination = Path.GetFullPath(args[3]);
            RequireInput(source);
            RequireInput(input);
            if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(input, destination, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The output must not overwrite an input file.");

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            WriteAtomically(destination, output =>
            {
                if (args[0] == "create")
                    BinaryPatch.Create(File.ReadAllBytes(source), File.ReadAllBytes(input), output);
                else
                {
                    using var oldFile = File.OpenRead(source);
                    BinaryPatch.Apply(oldFile, () => File.OpenRead(input), output);
                }
            });
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void WriteAtomically(string destination, Action<FileStream> write)
    {
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 1024 * 1024, FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                write(output);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void RequireInput(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Input file was not found.", path);
    }
}
