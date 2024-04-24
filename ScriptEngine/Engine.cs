using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
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

        // Cache for holding compiled assemblies.
        private readonly ConcurrentDictionary<string, Assembly> _cache = new ConcurrentDictionary<string, Assembly>();
        private readonly string _scriptsDirectory, _dllsDirectory;
        // File system watcher to monitor changes in scripts.
        private readonly FileSystemWatcher _fileWatcherScripts;
        // Debug mode flag and flag to allow unsafe code during compilation.
        private readonly bool _debug, _allowUnsafe, _enableDllCompilation;
        // Dictionary to store file hashes for determining script changes.
        private ConcurrentDictionary<string, string> _fileHashes = new ConcurrentDictionary<string, string>();

        #endregion

        #region Constructor

        /// <summary>
        /// Initializes the script engine with specified directories for scripts and DLLs.
        /// </summary>
        /// <param name="scriptsDirectory">Directory containing the scripts.</param>
        /// <param name="dllsDirectory">Directory containing additional DLLs for reference.</param>
        /// <param name="debug">Enable debug mode.</param>
        /// <param name="allowUnsafe">Allow unsafe code compilation.</param>
        public Engine(string scriptsDirectory, string dllsDirectory = "", bool debug = false, bool allowUnsafe = false, bool enableDllCompilation = true)
        {
            _allowUnsafe = allowUnsafe;
            _debug = debug;
            _dllsDirectory = dllsDirectory;
            _scriptsDirectory = scriptsDirectory;
            _enableDllCompilation = enableDllCompilation;

            // Subscribe to assembly load and resolve events.
            AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;
            AppDomain.CurrentDomain.AssemblyLoad += CurrentDomain_AssemblyLoad;

            // Compile scripts initially.
            CompileScripts();

            // Set up a file watcher to monitor script file changes.
            _fileWatcherScripts = new FileSystemWatcher(_scriptsDirectory, "*.cs")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };
            _fileWatcherScripts.Changed += OnScriptFileChanged;
        }


        #endregion

        #region Compilation Logic

        /// <summary>
        /// Compiles or loads all scripts from the scripts directory based on changes detected through hashing.
        /// </summary>
        private void CompileScripts()
        {
            if (!Directory.Exists(_scriptsDirectory))
            {
                Console.WriteLine($"Script directory '{_scriptsDirectory}' not found.");
                return;
            }

            var scriptFiles = Directory.GetFiles(_scriptsDirectory, "*.cs", SearchOption.AllDirectories);
            bool jsonExist = LoadHashInfo();

            Parallel.ForEach(scriptFiles, file =>
            {
                string fileName = Path.GetFileNameWithoutExtension(file);
                string fileHash = CalculateFileHash(file);

                if (_enableDllCompilation && jsonExist && _fileHashes.TryGetValue(fileName, out string storedHash) && storedHash == fileHash)
                {
                    Console.WriteLine($"Loading compiled script from disk for '{fileName}' as the hash is unchanged.");
                    LoadAssemblyFromFile(fileName, file);
                }
                else
                {
                    Console.WriteLine($"Compiling script: {file}");
                    CompileAndCache(file, fileName, AppDomain.CurrentDomain.GetAssemblies());
                }
            });
            if (_enableDllCompilation)
                UpdateHashInfo();
        }

        /// <summary>
        /// Compiles a script file and caches or loads the resulting assembly based on hash comparison.
        /// </summary>
        /// <param name="filePath">Path to the script file.</param>
        /// <param name="fileName">Script file name without extension.</param>
        /// <param name="loadedAssemblies">Already loaded assemblies in the current domain.</param>
        private void CompileAndCache(string filePath, string fileName, Assembly[] loadedAssemblies)
        {
            string fileHash = CalculateFileHash(filePath);
            _fileHashes[fileName] = fileHash;

            SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(File.ReadAllText(filePath));
            MetadataReference[] references = loadedAssemblies.Where(assembly => !string.IsNullOrEmpty(assembly.Location))
                                                             .Select(assembly => MetadataReference.CreateFromFile(assembly.Location))
                                                             .ToArray();
            CSharpCompilation compilation = CSharpCompilation.Create(fileName)
                .WithOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: _allowUnsafe))
                .AddReferences(references)
                .AddSyntaxTrees(syntaxTree);

            using (var ms = new MemoryStream())
            {
                EmitResult result = compilation.Emit(ms);
                if (!result.Success)
                {
                    Console.WriteLine($"Compilation failed for file '{fileName}': {string.Join(", ", result.Diagnostics.Select(d => d.GetMessage()))}");
                    return;
                }

                string scriptSubdirectory = Path.GetDirectoryName(filePath);
                string outputFilePath = "";
                if (_enableDllCompilation)
                {
                    string compiledDirectory = Path.Combine(scriptSubdirectory, "!compiled");
                    Directory.CreateDirectory(compiledDirectory);
                    outputFilePath = Path.Combine(compiledDirectory, $"{fileName}.dll");
                    File.WriteAllBytes(outputFilePath, ms.ToArray());
                }

                Assembly assembly = Assembly.Load(ms.ToArray());
                _cache[fileName] = assembly;
                if (_enableDllCompilation)
                    Console.WriteLine($"Script compiled and saved: {outputFilePath}");
            }
        }

        /// <summary>
        /// Loads an assembly from a previously compiled DLL file if it exists, or triggers recompilation if it does not.
        /// This method ensures that scripts are only recompiled when necessary, enhancing performance by reusing previously compiled results.
        /// </summary>
        /// <param name="fileName">The file name of the script without the .cs extension.</param>
        /// <param name="filePath">The absolute path to the script file. This path is expected to come from the file system watcher or the initial compilation process.</param>
        private void LoadAssemblyFromFile(string fileName, string filePath)
        {
            // Extract the directory part from the script's file path to locate or create the compiled DLLs directory.
            string scriptSubdirectory = Path.GetDirectoryName(filePath);

            // Construct the path to the directory where compiled DLLs are stored.
            // Use 'Path.GetFullPath' to normalize the path and ensure there are no relative path issues.
            string compiledDirectory = Path.Combine(scriptSubdirectory, "!compiled");
            string fullCompiledDirectory = Path.GetFullPath(compiledDirectory);  // This resolves any relative path issues and normalizes the path.

            // Construct the full path to the expected compiled DLL file using the normalized directory path.
            string outputFilePath = Path.Combine(fullCompiledDirectory, $"{fileName}.dll");

            // Check if the compiled DLL already exists at the specified location.
            if (File.Exists(outputFilePath))
            {
                // If debugging is enabled, log the action of loading the assembly.
                if (_debug)
                    Console.WriteLine($"Loading assembly from {outputFilePath}");

                // Load the assembly from the file.
                Assembly assembly = Assembly.LoadFile(outputFilePath);

                // Store the loaded assembly in the cache for quick retrieval in future requests.
                _cache[fileName] = assembly;
            }
            else
            {
                // If the DLL does not exist, log the need to recompile and proceed with recompilation.
                Console.WriteLine($"Compiled file not found, recompiling: {outputFilePath}");

                // Recompile the script file to update the assembly and the cache.
                CompileAndCache(filePath, fileName, AppDomain.CurrentDomain.GetAssemblies());
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
                // Recompile script
                CompileAndCache(filePath, fileName, loadedAssemblies);

                // Update hash info
                string fileHash = CalculateFileHash(filePath);
                UpdateHashInfo();

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
        #endregion

        #region Hash Logic

        /// <summary>
        /// Calculates the SHA256 hash of a file.
        /// </summary>
        private string CalculateFileHash(string filePath)
        {
            using (var stream = File.OpenRead(filePath))
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] hashBytes = sha.ComputeHash(stream);
                return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>
        /// Saves the current script file hashes to a JSON file for future reference.
        /// </summary>
        private void UpdateHashInfo()
        {
            string hashFilePath = Path.Combine(_scriptsDirectory, "hash_info.json");
            string json = JsonSerializer.Serialize(_fileHashes, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(hashFilePath, json);
            if (_debug)
                Console.WriteLine("Hash information saved successfully.");
        }

        /// <summary>
        /// Loads previously saved hash information from a JSON file.
        /// </summary>
        private bool LoadHashInfo()
        {
            string hashFilePath = Path.Combine(_scriptsDirectory, "hash_info.json");
            if (!File.Exists(hashFilePath)) return false;

            string json = File.ReadAllText(hashFilePath);
            _fileHashes = JsonSerializer.Deserialize<ConcurrentDictionary<string, string>>(json) ?? new ConcurrentDictionary<string, string>();
            return true;
        }

        #endregion
    }
}
