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

        /// <summary>
        /// The engine's own native runtime folders - GDAL's SWIG wraps, NFDRS4, FOFEM - under <c>Runtimes/Native</c>
        /// beside PREACTcore.dll, where every build copies them.
        /// </summary>
        public static string[] EngineRuntimeFolders()
        {
            string root = Path.Combine(Path.GetDirectoryName(typeof(NativeLibraries).Assembly.Location) ?? ".", "Runtimes", "Native");
            return new[]
            {
                Path.Combine(root, "FOFEM", "x64"),
                Path.Combine(root, "GDAL", "x64"),
                Path.Combine(root, "NFDRS4", "x64"),
            };
        }

        /// <summary>
        /// What the <see cref="Engine"/> sets up for a process, for one that uses the engine's GDAL code without
        /// building an Engine - PREACTcli: the resolver over <see cref="EngineRuntimeFolders"/> for the engine and the
        /// GDAL wrapper assemblies (plus <paramref name="extraFolders"/>), PROJ's search path, and GDAL's drivers.
        /// </summary>
        /// <remarks>
        /// PREACTcli used to die on Linux in its first GDAL call with "The type initializer for
        /// 'OSGeo.OSR.OsrPINVOKE' threw an exception": its build copies only the Windows <c>*.dll</c> wraps beside
        /// the executable, the <c>lib*_wrap.so</c> stay in <c>Runtimes/Native/GDAL/x64</c>, and only the Engine
        /// constructor ever registered the resolver that finds them - so every Linux user had to put that folder on
        /// <c>LD_LIBRARY_PATH</c>. Only libgdal itself (GDAL 3.10's libgdal.so.36) still has to be findable by the
        /// platform loader.
        /// </remarks>
        public static void SetUpForProcess(IEnumerable<string> extraFolders = null)
        {
            var folders = new List<string>(EngineRuntimeFolders());
            if (extraFolders != null) folders.AddRange(extraFolders);

            Register(folders, typeof(NativeLibraries).Assembly, typeof(OSGeo.GDAL.Gdal).Assembly,
                typeof(OSGeo.OGR.Ogr).Assembly, typeof(OSGeo.OSR.Osr).Assembly);

            //The process environment, which on Windows already holds the Machine and User values it was started
            //with. Only when something was found: an empty list would replace PROJ's own compiled-in search path.
            var projPaths = new List<string>();
            foreach (string candidate in new[] { Environment.GetEnvironmentVariable("PROJ_DATA"), Environment.GetEnvironmentVariable("PROJ_LIB") })
            {
                if (!string.IsNullOrEmpty(candidate) && Directory.Exists(candidate) && !projPaths.Contains(candidate))
                {
                    projPaths.Add(candidate);
                }
            }
            if (projPaths.Count == 0 && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Directory.Exists("/usr/share/proj"))
            {
                projPaths.Add("/usr/share/proj");
            }
            if (projPaths.Count > 0)
            {
                OSGeo.OSR.Osr.SetPROJSearchPaths(projPaths.ToArray());
            }

            OSGeo.GDAL.Gdal.AllRegister();
            OSGeo.OGR.Ogr.RegisterAll();
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
