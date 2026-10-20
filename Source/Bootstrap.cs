using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace JustEnoughTools
{
    // Dependencies are PE resources, never files extracted next to the application.
    public static class DependencyBootstrap
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Assembly> Loaded = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        private static bool initialized;

        public static void Initialize()
        {
            lock (Gate)
            {
                if (initialized) return;
                AppDomain.CurrentDomain.AssemblyResolve += Resolve;
                initialized = true;
            }
        }

        private static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            var name = new AssemblyName(args.Name);
            if (string.IsNullOrEmpty(name.Name) || name.Name.IndexOfAny(new[] { '/', '\\' }) >= 0) return null;
            lock (Gate)
            {
                Assembly existing;
                if (Loaded.TryGetValue(name.Name, out existing)) return existing;
                using (var stream = typeof(DependencyBootstrap).Assembly.GetManifestResourceStream("Dependencies." + name.Name + ".dll"))
                {
                    if (stream == null) return null;
                    using (var bytes = new MemoryStream())
                    {
                        stream.CopyTo(bytes);
                        var assembly = Assembly.Load(bytes.ToArray());
                        Loaded[name.Name] = assembly;
                        return assembly;
                    }
                }
            }
        }
    }
}
