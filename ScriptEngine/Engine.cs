using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace ScriptEngine
{
    /// <summary>
    /// Provides functionality to load and execute C# scripts dynamically.
    /// </summary>
    public class Engine
    {
        #region Fields

        private readonly ConcurrentDictionary<string, Assembly> _cache = new ConcurrentDictionary<string, Assembly>();
        private readonly string _scriptsDirectory, _dllsDirectory;
        private readonly FileSystemWatcher _fileWatcherScripts;
        private readonly bool _debug;
        private readonly bool _allowUnsafe;

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes a new instance of the <see cref="CodeLoader"/> class.
        /// </summary>
        /// <param name="scriptsDirectory">The directory containing the scripts to load.</param>
        /// <param name="dllsDirectory">The directory containing additional DLLs to reference.</param>
        public Engine(string scriptsDirectory, string dllsDirectory = "", bool debug = false, bool allowUnsafe = false)
        {
            _allowUnsafe = allowUnsafe;
            _debug = debug;
            _dllsDirectory = dllsDirectory;
            _scriptsDirectory = scriptsDirectory;

            // Subscribe to AppDomain events
            AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;
            AppDomain.CurrentDomain.AssemblyLoad += CurrentDomain_AssemblyLoad;

            // Compile scripts initially
            CompileScripts();

            // Setup file watcher for script changes
            _fileWatcherScripts = new FileSystemWatcher(_scriptsDirectory, "*.cs");
            _fileWatcherScripts.IncludeSubdirectories = true;
            _fileWatcherScripts.NotifyFilter = NotifyFilters.LastWrite;
            _fileWatcherScripts.Changed += OnScriptFileChanged;
            _fileWatcherScripts.EnableRaisingEvents = true;
        }

        #endregion

        #region Compilation

        /// <summary>
        /// Compiles all scripts found in the scripts directory and its subdirectories.
        /// </summary>
        private void CompileScripts()
        {
            try
            {
                // Check if the scripts directory exists
                if (Directory.Exists(_scriptsDirectory))
                {
                    // Get all script files recursively
                    var scriptFiles = Directory.GetFiles(_scriptsDirectory, "*.cs", SearchOption.AllDirectories);

                    // Compile each script in parallel
                    Parallel.ForEach(scriptFiles, file =>
                    {
                        Console.WriteLine($"Compiling script: {file}");
                        string fileName = Path.GetFileNameWithoutExtension(file);
                        CompileAndCache(file, fileName, AppDomain.CurrentDomain.GetAssemblies());
                    });
                }
                else
                {
                    Console.WriteLine($"Script directory '{_scriptsDirectory}' not found.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error compiling scripts: {ex.Message}");
            }
        }

        /// <summary>
        /// Compiles a script file and caches the resulting assembly.
        /// </summary>
        /// <param name="filePath">The path to the script file.</param>
        /// <param name="fileName">The name of the script file.</param>
        /// <param name="loadedAssemblies">The assemblies already loaded in the current application domain.</param>
        private void CompileAndCache(string filePath, string fileName, Assembly[] loadedAssemblies)
        {
            try
            {
                // Parse the syntax tree from the script file
                SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(File.ReadAllText(filePath));

                // Create metadata references for loaded assemblies
                MetadataReference[] references = loadedAssemblies
                    .Where(assembly => !string.IsNullOrEmpty(assembly.Location))
                    .Select(assembly => MetadataReference.CreateFromFile(assembly.Location))
                    .ToArray();

                // Create compilation with syntax tree and references
                CSharpCompilation compilation = CSharpCompilation.Create(fileName)
                    .WithOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: _allowUnsafe))
                    .AddReferences(references)
                    .AddSyntaxTrees(syntaxTree);

                using (var ms = new MemoryStream())
                {
                    // Emit the compiled assembly
                    EmitResult result = compilation.Emit(ms);
                    if (!result.Success)
                    {
                        Console.WriteLine($"Compilation failed for file '{fileName}':");
                        foreach (var diagnostic in result.Diagnostics)
                        {
                            Console.WriteLine($"{diagnostic.Id}: {diagnostic.GetMessage()}");
                        }
                        return;
                    }

                    ms.Seek(0, SeekOrigin.Begin);
                    // Load the assembly into memory
                    Assembly assembly = Assembly.Load(ms.ToArray());
                    _cache[fileName] = assembly;
                    Console.WriteLine($"Script compiled and cached: {fileName}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error compiling script {filePath}: {ex.Message}");
            }
        }

        #endregion

        #region File System Watcher

        /// <summary>
        /// Handles script file changes and triggers recompilation.
        /// </summary>
        private void OnScriptFileChanged(object sender, FileSystemEventArgs e)
        {
            try
            {
                Thread.Sleep(1000); // Wait for file operations to complete

                string filePath = e.FullPath;
                string fileName = Path.GetFileName(filePath);
                string codeId = Path.GetFileNameWithoutExtension(fileName);
                var loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();
                Console.WriteLine($"Reloading script: {filePath}\\{fileName}");
                CompileAndCache(filePath, Path.GetFileNameWithoutExtension(fileName), loadedAssemblies);

                if (_cache.ContainsKey(codeId))
                {
                    Console.WriteLine($"Reloaded script: {filePath}\\{fileName}");
                }
                else
                {
                    Console.WriteLine($"New assembly for '{codeId}' could not be compiled or loaded.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling script change: {ex.Message}");
            }
        }

        #endregion

        #region Assembly Events

        /// <summary>
        /// Resolves assembly references by searching in the DLLs directory.
        /// </summary>
        private Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            try
            {
                string assemblyName = new AssemblyName(args.Name).Name;
                Assembly loadedAssembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == assemblyName);

                if (loadedAssembly != null)
                {
                    Console.WriteLine($"Assembly found in memory: {assemblyName}");
                    return loadedAssembly;
                }

                string assemblyPath = Path.Combine(_dllsDirectory, assemblyName + ".dll");
                if (string.IsNullOrEmpty(_dllsDirectory))
                    assemblyPath = $"{assemblyName}.dll";
                if (File.Exists(assemblyPath))
                {
                    Console.WriteLine($"Attempting to resolve assembly: {assemblyName} from path: {assemblyPath}");
                    return Assembly.LoadFile(assemblyPath);
                }

                Console.WriteLine($"Assembly not found: {assemblyName}");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error resolving assembly: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Handles the event when an assembly is loaded into the application domain.
        /// </summary>
        private void CurrentDomain_AssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            try
            {
                if (_debug)
                    Console.WriteLine($"Assembly loaded: {args.LoadedAssembly.FullName}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error logging assembly load: {ex.Message}");
            }
        }

        #endregion

        #region Execute Function

        /// <summary>
        /// Executes a function from a script.
        /// </summary>
        /// <typeparam name="T">The return type of the function.</typeparam>
        /// <param name="script">The file name of the script without .cs</param>
        /// <param name="className">The name of the class containing the function.</param>
        /// <param name="function">The name of the function to execute.</param>
        /// <param name="parameters">Optional parameters for the function.</param>
        /// <returns>The result of the function execution.</returns>
        public T? ExecuteFunction<T>(string script, string nameSpace, string className, string function, params object[] parameters)
        {
            try
            {
                // Check if the parameters are valid and script exists in cache
                if (script != null && className != null && function != null && _cache.TryGetValue(script, out Assembly assembly))
                {
                    Type type = assembly.GetType($"{nameSpace}.{className}");
                    if (string.IsNullOrEmpty(nameSpace))
                        type = assembly.GetType(className);
                    if (type != null)
                    {
                        MethodInfo method = type.GetMethod(function, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);
                        if (method != null)
                        {
                            object classInstance = method.IsStatic ? null : Activator.CreateInstance(type);
                            object result = method.Invoke(classInstance, parameters);
                            return (T)result;
                        }
                        else
                        {
                            Console.WriteLine($"Method '{function}' not found in type '{className}'.");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"Type '{className}' not found in assembly.");
                    }
                }
                else
                {
                    Console.WriteLine($"Invalid parameters or script '{script}' not found or not compiled.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error executing function: {ex.Message}");
            }

            return default(T);
        }

        /// <summary>
        /// Executes a void function from a script.
        /// </summary>
        /// <param name="script">The file name of the script without .cs</param>
        /// <param name="className">The name of the class containing the function.</param>
        /// <param name="function">The name of the function to execute.</param>
        /// <param name="parameters">Optional parameters for the function.</param>
        public void ExecuteFunction(string script, string nameSpace, string className, string function, params object[] parameters)
        {
            try
            {
                // Check if the parameters are valid and script exists in cache
                if (script != null && className != null && function != null && _cache.TryGetValue(script, out Assembly assembly))
                {
                    Type type = assembly.GetType($"{nameSpace}.{className}");
                    if (string.IsNullOrEmpty(nameSpace))
                        type = assembly.GetType(className);
                    if (type != null)
                    {
                        MethodInfo method = type.GetMethod(function, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);
                        if (method != null)
                        {
                            // Execute void function
                            object classInstance = method.IsStatic ? null : Activator.CreateInstance(type);
                            method.Invoke(classInstance, parameters);
                        }
                        else
                        {
                            Console.WriteLine($"Method '{function}' not found in type '{className}'.");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"Type '{className}' not found in assembly.");
                    }
                }
                else
                {
                    Console.WriteLine($"Invalid parameters or script '{script}' not found or not compiled.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error executing void function: {ex.Message}");
            }
        }
    }
    #endregion
}
