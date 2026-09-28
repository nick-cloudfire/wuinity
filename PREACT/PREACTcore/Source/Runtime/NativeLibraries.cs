//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace PREACT.Runtime
{
    /// <summary>
    /// Makes the native libraries committed under <c>Runtimes/Native</c> (GDAL wraps, NFDRS4, FOFEM) and SUMO's
    /// <c>libsumocs</c> findable by P/Invoke on .NET off Windows.
    /// </summary>
    /// <remarks>
    /// On Windows the engine prepends those folders to the process PATH, and <c>LoadLibrary</c> honours that at
    /// any time. The Linux and macOS loaders do not: <c>LD_LIBRARY_PATH</c> is read once, when the process
    /// starts, so setting it from inside the process - which is what the engine used to do - changes nothing for
    /// the process itself. What does work is a resolver on each assembly that declares the imports, which .NET
    /// (Core) supports through <c>NativeLibrary.SetDllImportResolver</c>.
    ///
    /// That API is not part of netstandard2.1, and Unity's Mono does not have it, so it is looked up by
    /// reflection and silently skipped where it does not exist. Under Mono nothing changes.
    ///
    /// A library's own dependencies are still found by the platform loader: <c>libgdal_wrap.so</c> needs
    /// <c>libgdal.so.36</c> (GDAL 3.10), which has to be on <c>LD_LIBRARY_PATH</c> or in the loader cache.
    /// </remarks>
    public static class NativeLibraries
    {
        private static readonly object _lock = new object();
        private static readonly HashSet<Assembly> _registered = new HashSet<Assembly>();
        private static string[] _searchFolders = Array.Empty<string>();

        /// <summary>
        /// Registers the resolver for the assemblies that P/Invoke into the libraries in
        /// <paramref name="searchFolders"/>. Safe to call more than once; later calls only add folders.
        /// </summary>
        public static void Register(IEnumerable<string> searchFolders, params Assembly[] assemblies)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return;
            }

            lock (_lock)
            {
                var folders = new List<string>(_searchFolders);
                foreach (string folder in searchFolders)
                {
                    if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder) && !folders.Contains(folder))
                    {
                        folders.Add(folder);
                    }
                }
                _searchFolders = folders.ToArray();

                Assembly coreLib = typeof(Marshal).Assembly;
                Type nativeLibrary = coreLib.GetType("System.Runtime.InteropServices.NativeLibrary");
                Type resolverType = coreLib.GetType("System.Runtime.InteropServices.DllImportResolver");
                if (nativeLibrary == null || resolverType == null)
                {
                    return; //Mono, or anything else without the API: the platform's own probing is all there is
                }

                MethodInfo setResolver = nativeLibrary.GetMethod("SetDllImportResolver", new[] { typeof(Assembly), resolverType });
                MethodInfo resolve = typeof(NativeLibraries).GetMethod(nameof(Resolve), BindingFlags.NonPublic | BindingFlags.Static);
                if (setResolver == null || resolve == null)
                {
                    return;
                }

                Delegate resolver = Delegate.CreateDelegate(resolverType, resolve);
                foreach (Assembly assembly in assemblies)
                {
                    if (assembly == null || _registered.Contains(assembly))
                    {
                        continue;
                    }

                    try
                    {
                        setResolver.Invoke(null, new object[] { assembly, resolver });
                        _registered.Add(assembly);
                    }
                    catch (Exception)
                    {
                        //Another resolver was set for this assembly first (a host application's own). That one
                        //wins; ours is a convenience, not a requirement.
                    }
                }
            }
        }

        private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
        {
            string[] folders = _searchFolders;
            foreach (string folder in folders)
            {
                foreach (string candidate in CandidateFileNames(libraryName))
                {
                    string path = Path.Combine(folder, candidate);
                    if (File.Exists(path) && TryLoad(path, out IntPtr handle))
                    {
                        return handle;
                    }
                }
            }

            //Not ours: let the runtime's default probing (application folder, LD_LIBRARY_PATH, loader cache) try.
            return IntPtr.Zero;
        }

        private static IEnumerable<string> CandidateFileNames(string libraryName)
        {
            bool mac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
            string extension = mac ? ".dylib" : ".so";
            yield return libraryName;
            if (!libraryName.StartsWith("lib", StringComparison.Ordinal))
            {
                yield return "lib" + libraryName + extension;
            }
            yield return libraryName + extension;
        }

        private static bool TryLoad(string path, out IntPtr handle)
        {
            handle = IntPtr.Zero;
            Type nativeLibrary = typeof(Marshal).Assembly.GetType("System.Runtime.InteropServices.NativeLibrary");
            MethodInfo tryLoad = nativeLibrary?.GetMethod("TryLoad", new[] { typeof(string), typeof(IntPtr).MakeByRefType() });
            if (tryLoad == null)
            {
                return false;
            }

            object[] arguments = { path, IntPtr.Zero };
            bool loaded = (bool)tryLoad.Invoke(null, arguments);
            if (loaded)
            {
                handle = (IntPtr)arguments[1];
            }
            return loaded;
        }
    }
}
