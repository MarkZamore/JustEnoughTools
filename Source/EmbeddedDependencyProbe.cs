using System;
using System.IO;
using System.Linq;
using System.Text;
using BsDiff;

namespace JustEnoughTools
{
    internal static class EmbeddedDependencyProbe
    {
        internal static void Run()
        {
            byte[] original = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("JustEnoughTools portable application\n", 64)));
            byte[] updated = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("JustEnoughTools portable update\n", 65)));
            byte[] patch;
            using (var stream = new MemoryStream())
            {
                BinaryPatch.Create(original, updated, stream);
                patch = stream.ToArray();
            }
            using (var input = new MemoryStream(original, false))
            using (var output = new MemoryStream())
            {
                BinaryPatch.Apply(input, () => new MemoryStream(patch, false), output);
                if (!updated.SequenceEqual(output.ToArray())) throw new InvalidOperationException("Embedded delta dependencies failed their roundtrip.");
            }
        }
    }
}
