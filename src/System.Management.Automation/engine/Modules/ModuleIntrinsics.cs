/********************************************************************++
Copyright (c) Microsoft Corporation.  All rights reserved.
--********************************************************************/

using System.Collections.Generic;
using System.IO;
using System.Management.Automation.Internal;
using System.Management.Automation.Language;
using System.Management.Automation.Runspaces;
using Microsoft.PowerShell.Commands;
using System.Linq;
using System.Threading;
using Dbg = System.Management.Automation.Diagnostics;

namespace System.Management.Automation
{
    /// <summary>
    /// Encapsulates the basic module operations for a PowerShell engine instance...
    /// </summary>
    public class ModuleIntrinsics
    {
        /// <summary>
        /// Tracer for module analysis
        /// </summary>
        [TraceSource("Modules", "Module loading and analysis")]
        internal static PSTraceSource Tracer = PSTraceSource.GetTracer("Modules", "Module loading and analysis");

        internal ModuleIntrinsics(ExecutionContext context)
        {
            _context = context;

            // And initialize the module path...
            SetModulePath();
        }
        private readonly ExecutionContext _context;

        // Holds the module collection...
        internal Dictionary<string, PSModuleInfo> ModuleTable
        {
            get
            {
                return _moduleTable;
            }
        }
        private readonly Dictionary<string, PSModuleInfo> _moduleTable = new Dictionary<string, PSModuleInfo>(StringComparer.OrdinalIgnoreCase);

        const int MaxModuleNestingDepth = 10;

        internal void IncrementModuleNestingDepth(PSCmdlet cmdlet, string path)
        {
            if (++_moduleNestingDepth > MaxModuleNestingDepth)
            {
                string message = StringUtil.Format(Modules.ModuleTooDeeplyNested, path, MaxModuleNestingDepth);
                InvalidOperationException ioe = new InvalidOperationException(message);
                ErrorRecord er = new ErrorRecord(ioe, "Modules_ModuleTooDeeplyNested",
                    ErrorCategory.InvalidOperation, path);
                // NOTE: this call will throw
                cmdlet.ThrowTerminatingError(er);
            }
        }
        internal void DecrementModuleNestingCount()
        {
            --_moduleNestingDepth;
        }

        internal int ModuleNestingDepth
        {
            get { return _moduleNestingDepth; }
        }

        int _moduleNestingDepth;

        /// <summary>
        /// Create a new module object from a scriptblock specifying the path to set for the module
        /// </summary>
        /// <param name="name">The name of the module</param>
        /// <param name="path">The path where the module is rooted</param>
        /// <param name="scriptBlock">
        /// ScriptBlock that is executed to initialize the module...
        /// </param>
        /// <param name="arguments">
        /// The arguments to pass to the scriptblock used to initialize the module
        /// </param>
        /// <param name="ss">The session state instance to use for this module - may be null</param>
        /// <param name="results">The results produced from evaluating the scriptblock</param>
        /// <returns>The newly created module info object</returns>
        internal PSModuleInfo CreateModule(string name, string path, ScriptBlock scriptBlock, SessionState ss, out List<object> results, params object[] arguments)
        {
            return CreateModuleImplementation(name, path, scriptBlock, null, ss, null, out results, arguments);
        }

        /// <summary>
        /// Create a new module object from a ScriptInfo object
        /// </summary>
        /// <param name="path">The path where the module is rooted</param>
        /// <param name="scriptInfo">The script info to use to create the module</param>
        /// <param name="scriptPosition">The position for the command that loaded this module</param>
        /// <param name="arguments">Optional arguments to pass to the script while executing</param>
        /// <param name="ss">The session state instance to use for this module - may be null</param>
        /// <param name="privateData">The private data to use for this module - may be null</param>
        /// <returns>The constructed module object</returns>
        internal PSModuleInfo CreateModule(string path, ExternalScriptInfo scriptInfo, IScriptExtent scriptPosition, SessionState ss, object privateData, params object[] arguments)
        {
            List<object> result;
            return CreateModuleImplementation(ModuleIntrinsics.GetModuleName(path), path, scriptInfo, scriptPosition, ss, privateData, out result, arguments);
        }

        /// <summary>
        /// Create a new module object from code specifying the path to set for the module
        /// </summary>
        /// <param name="name">The name of the module</param>
        /// <param name="path">The path to use for the module root</param>
        /// <param name="moduleCode">
        /// The code to use to create the module. This can be one of ScriptBlock, string 
        /// or ExternalScriptInfo
        /// </param>
        /// <param name="arguments">
        /// Arguments to pass to the module scriptblock during evaluation.
        /// </param>
        /// <param name="result">
        /// The results of the evaluation of the scriptblock.
        /// </param>
        /// <param name="scriptPosition">
        /// The position of the caller of this function so you can tell where the call
        /// to Import-Module (or whatever) occurred. This can be null.
        /// </param>
        /// <param name="ss">The session state instance to use for this module - may be null</param>
        /// <param name="privateData">The private data to use for this module - may be null</param>
        /// <returns>The created module</returns>
        private PSModuleInfo CreateModuleImplementation(string name, string path, object moduleCode, IScriptExtent scriptPosition, SessionState ss, object privateData, out List<object> result, params object[] arguments)
        {
            ScriptBlock sb;

            // By default the top-level scope in a session state object is the global scope for the instance.
            // For modules, we need to set its global scope to be another scope object and, chain the top
            // level scope for this sessionstate instance to be the parent. The top level scope for this ss is the
            // script scope for the ss.

            // Allocate the session state instance for this module.
            if (ss == null)
            {
                ss = new SessionState(_context, true, true);
            }

            // Now set up the module's session state to be the current session state
            SessionStateInternal oldSessionState = _context.EngineSessionState;
            PSModuleInfo module = new PSModuleInfo(name, path, _context, ss);
            ss.Internal.Module = module;
            module.PrivateData = privateData;

            bool setExitCode = false;
            int exitCode = 0;

            try
            {
                _context.EngineSessionState = ss.Internal;

                // Build the scriptblock at this point so the references to the module
                // context are correct...
                ExternalScriptInfo scriptInfo = moduleCode as ExternalScriptInfo;
                if (scriptInfo != null)
                {
                    sb = scriptInfo.ScriptBlock;

                    _context.Debugger.RegisterScriptFile(scriptInfo);
                }
                else
                {
                    sb = moduleCode as ScriptBlock;
                    if (sb != null)
                    {
                        PSLanguageMode? moduleLanguageMode = sb.LanguageMode;
                        sb = sb.Clone();
                        sb.LanguageMode = moduleLanguageMode;

                        sb.SessionState = ss;
                    }
                    else
                    {
                        var sbText = moduleCode as string;
                        if (sbText != null)
                            sb = ScriptBlock.Create(_context, sbText);
                    }
                }
                if (sb == null)
                    throw PSTraceSource.NewInvalidOperationException();

                sb.SessionStateInternal = ss.Internal;

                InvocationInfo invocationInfo = new InvocationInfo(scriptInfo, scriptPosition);

                // Save the module string 
                module._definitionExtent = sb.Ast.Extent;
                var ast = sb.Ast;
                while (ast.Parent != null)
                {
                    ast = ast.Parent;
                }
                
                // The variables set in the interpretted case get set by InvokeWithPipe in the compiled case.
                Diagnostics.Assert(_context.SessionState.Internal.CurrentScope.LocalsTuple == null,
                                    "No locals tuple should have been created yet.");

                List<object> resultList = new List<object>();

                try
                {
                    Pipe outputPipe = new Pipe(resultList);

                    // And run the scriptblock...
                    sb.InvokeWithPipe(
                        useLocalScope:         false,
                        errorHandlingBehavior: ScriptBlock.ErrorHandlingBehavior.WriteToCurrentErrorPipe,
                        dollarUnder:           AutomationNull.Value,
                        input:                 AutomationNull.Value,
                        scriptThis:            AutomationNull.Value,
                        outputPipe:            outputPipe,
                        invocationInfo:        invocationInfo,
                        args:                  arguments ?? Utils.EmptyArray<object>());
                }
                catch (ExitException ee)
                {
                    exitCode = (int)ee.Argument;
                    setExitCode = true;
                }
                result = resultList;
            }
            finally
            {
                _context.EngineSessionState = oldSessionState;
            }

            if (setExitCode)
            {
                _context.SetVariable(SpecialVariables.LastExitCodeVarPath, exitCode);
            }

            module.ImplementingAssembly = sb.AssemblyDefiningPSTypes;
            // We force re-population of ExportedTypeDefinitions, now with the actual RuntimeTypes, created above.
            module.CreateExportedTypeDefinitions(sb.Ast as ScriptBlockAst);

            return module;
        }

        /// <summary>
        /// Allocate a new dynamic module then return a new scriptblock
        /// bound to the module instance.
        /// </summary>
        /// <param name="context">Context to use to create bounded script.</param>
        /// <param name="sb">The scriptblock to bind</param>
        /// <param name="linkToGlobal">Whether it should be linked to the global session state or not</param>
        /// <returns>A new scriptblock</returns>
        internal ScriptBlock CreateBoundScriptBlock(ExecutionContext context, ScriptBlock sb, bool linkToGlobal)
        {
            PSModuleInfo module = new PSModuleInfo(context, linkToGlobal);
            return module.NewBoundScriptBlock(sb, context);
        }

        internal List<PSModuleInfo> GetModules(string[] patterns, bool all)
        {
            return GetModuleCore(patterns, all, false);
        }

        internal List<PSModuleInfo> GetExactMatchModules(string moduleName, bool all, bool exactMatch)
        {
            if (moduleName == null) { moduleName = String.Empty; }
            return GetModuleCore(new string[] {moduleName}, all, exactMatch);
        }

        private List<PSModuleInfo> GetModuleCore(string[] patterns, bool all, bool exactMatch)
        {
            string targetModuleName = null;
            List<WildcardPattern> wcpList = new List<WildcardPattern>();

            if (exactMatch)
            {
                Dbg.Assert(patterns.Length == 1, "The 'patterns' should only contain one element when it is for an exact match");
                targetModuleName = patterns[0];
            }
            else
            {
                if (patterns == null)
                {
                    patterns = new string[] { "*" };
                }

                foreach (string pattern in patterns)
                {
                    wcpList.Add(WildcardPattern.Get(pattern, WildcardOptions.IgnoreCase));
                }
            }

            List<PSModuleInfo> modulesMatched = new List<PSModuleInfo>();

            if (all)
            {
                foreach (PSModuleInfo module in ModuleTable.Values)
                {
                    // See if this is the requested module...
                    if ((exactMatch && module.Name.Equals(targetModuleName, StringComparison.OrdinalIgnoreCase)) || 
                        (!exactMatch && SessionStateUtilities.MatchesAnyWildcardPattern(module.Name, wcpList, false)))
                    {
                        modulesMatched.Add(module);
                    }
                }
            }
            else
            {
                // Create a joint list of local and global modules. Only report a module once.
                // Local modules are reported before global modules...
                Dictionary<string, bool> found = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in _context.EngineSessionState.ModuleTable)
                {
                    string path = pair.Key;
                    PSModuleInfo module = pair.Value;
                    // See if this is the requested module...
                    if ((exactMatch && module.Name.Equals(targetModuleName, StringComparison.OrdinalIgnoreCase)) ||
                        (!exactMatch && SessionStateUtilities.MatchesAnyWildcardPattern(module.Name, wcpList, false)))
                    {
                        modulesMatched.Add(module);
                        found[path] = true;
                    }
                }
                if (_context.EngineSessionState != _context.TopLevelSessionState)
                {
                    foreach (var pair in _context.TopLevelSessionState.ModuleTable)
                    {
                        string path = pair.Key;
                        if (!found.ContainsKey(path))
                        {
                            PSModuleInfo module = pair.Value;
                            // See if this is the requested module...
                            if ((exactMatch && module.Name.Equals(targetModuleName, StringComparison.OrdinalIgnoreCase)) ||
                                (!exactMatch && SessionStateUtilities.MatchesAnyWildcardPattern(module.Name, wcpList, false)))
                            {
                                modulesMatched.Add(module);
                            }
                        }
                    }
                }
            }

            return modulesMatched.OrderBy(m => m.Name).ToList();
        }

        internal List<PSModuleInfo> GetModules(ModuleSpecification[] fullyQualifiedName, bool all)
        {
            List<PSModuleInfo> modulesMatched = new List<PSModuleInfo>();

            if (all)
            {
                foreach (var moduleSpec in fullyQualifiedName)
                {
                    foreach (PSModuleInfo module in ModuleTable.Values)
                    {
                        // See if this is the requested module...
                        if (IsModuleMatchingModuleSpec(module, moduleSpec))
                        {
                            modulesMatched.Add(module);
                        }
                    }
                }
            }
            else
            {
                foreach (var moduleSpec in fullyQualifiedName)
                {
                    // Create a joint list of local and global modules. Only report a module once.
                    // Local modules are reported before global modules...
                    Dictionary<string, bool> found = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                    foreach (var pair in _context.EngineSessionState.ModuleTable)
                    {
                        string path = pair.Key;
                        PSModuleInfo module = pair.Value;
                        // See if this is the requested module...
                        if (IsModuleMatchingModuleSpec(module, moduleSpec))
                        {
                            modulesMatched.Add(module);
                            found[path] = true;
                        }
                    }

                    if (_context.EngineSessionState != _context.TopLevelSessionState)
                    {
                        foreach (var pair in _context.TopLevelSessionState.ModuleTable)
                        {
                            string path = pair.Key;
                            if (!found.ContainsKey(path))
                            {
                                PSModuleInfo module = pair.Value;
                                // See if this is the requested module...
                                if (IsModuleMatchingModuleSpec(module, moduleSpec))
                                {
                                    modulesMatched.Add(module);
                                }
                            }
                        }
                    }
                }
            }

            return modulesMatched.OrderBy(m => m.Name).ToList();
        }

        internal static bool IsModuleMatchingModuleSpec(PSModuleInfo moduleInfo, ModuleSpecification moduleSpec)
        {
            if (moduleInfo != null && moduleSpec != null &&
                moduleInfo.Name.Equals(moduleSpec.Name, StringComparison.OrdinalIgnoreCase) &&
                (!moduleSpec.Guid.HasValue || moduleSpec.Guid.Equals(moduleInfo.Guid)) &&
                ((moduleSpec.Version == null && moduleSpec.RequiredVersion == null && moduleSpec.MaximumVersion == null)
                 || (moduleSpec.RequiredVersion != null && moduleSpec.RequiredVersion.Equals(moduleInfo.Version))
                 || (moduleSpec.MaximumVersion == null && moduleSpec.Version != null && moduleSpec.RequiredVersion == null && moduleSpec.Version <= moduleInfo.Version)
                 || (moduleSpec.MaximumVersion != null && moduleSpec.Version == null && moduleSpec.RequiredVersion == null && ModuleCmdletBase.GetMaximumVersion(moduleSpec.MaximumVersion) >= moduleInfo.Version)
                 || (moduleSpec.MaximumVersion != null && moduleSpec.Version != null && moduleSpec.RequiredVersion == null && ModuleCmdletBase.GetMaximumVersion(moduleSpec.MaximumVersion) >= moduleInfo.Version && moduleSpec.Version <= moduleInfo.Version)))
            {
                return true;
            }

            return false;
        }

        internal static Version GetManifestModuleVersion(string manifestPath)
        {
            if (manifestPath != null && 
                manifestPath.EndsWith(StringLiterals.PowerShellDataFileExtension, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var dataFileSetting =
                        PsUtils.GetModuleManifestProperties(
                            manifestPath,
                            PsUtils.ManifestModuleVersionPropertyName);

                    var versionValue = dataFileSetting["ModuleVersion"];
                    if (versionValue != null)
                    {
                        Version moduleVersion;
                        if (LanguagePrimitives.TryConvertTo(versionValue, out moduleVersion))
                        {
                            return moduleVersion;
                        }
                    }
                }
                catch (PSInvalidOperationException)
                {
                }
            }

            return new Version(0, 0);
        }

        internal static Guid GetManifestGuid(string manifestPath)
        {
            if (manifestPath != null &&
                manifestPath.EndsWith(StringLiterals.PowerShellDataFileExtension, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var dataFileSetting =
                        PsUtils.GetModuleManifestProperties(
                            manifestPath,
                            PsUtils.ManifestGuidPropertyName);

                    var guidValue = dataFileSetting["GUID"];
                    if (guidValue != null)
                    {
                        Guid guidID;
                        if (LanguagePrimitives.TryConvertTo(guidValue, out guidID))
                        {
                            return guidID;
                        }
                    }
                }
                catch (PSInvalidOperationException)
                {
                }
            }

            return new Guid();
        }

        // The extensions of all of the files that can be processed with Import-Module
        internal static string[] PSModuleProcessableExtensions = new string[] {
                            StringLiterals.PowerShellDataFileExtension,
                            StringLiterals.PowerShellScriptFileExtension,
                            StringLiterals.PowerShellModuleFileExtension,
                            StringLiterals.PowerShellCmdletizationFileExtension,
                            StringLiterals.WorkflowFileExtension,
                            ".dll" };

        // A list of the extensions to check for implicit module loading and discovery
        internal static string[] PSModuleExtensions = new string[] {
                            StringLiterals.PowerShellDataFileExtension,
                            StringLiterals.PowerShellModuleFileExtension,
                            StringLiterals.PowerShellCmdletizationFileExtension,
                            StringLiterals.WorkflowFileExtension,
                            ".dll" };

        /// <summary>
        /// Returns true if the extension is one of the module extensions...
        /// </summary>
        /// <param name="extension">The extension to check</param>
        /// <returns>True if it was a module extension...</returns>
        internal static bool IsPowerShellModuleExtension(string extension)
        {
            foreach (string ext in PSModuleProcessableExtensions)
            {
                if (extension.Equals(ext, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Gets the module name from module path.
        /// </summary>
        /// <param name="path">The path to the module</param>
        /// <returns>The module name</returns>
        internal static string GetModuleName(string path)
        {
            string fileName = path == null ? string.Empty : Path.GetFileName(path);
            string ext = Path.GetExtension(fileName);
            if (!string.IsNullOrEmpty(ext) && IsPowerShellModuleExtension(ext))
            {
                return fileName.Substring(0, fileName.Length - ext.Length);
            }
            else
            {
                return fileName;
            }
        }

        /// <summary>
        /// Gets the personal module path
        /// (i.e. C:\Users\lukasza\Documents\WindowsPowerShell\modules, or
        /// ~/.powershell/Modules on Linux)
        /// </summary>
        /// <returns>personal module path</returns>
        internal static string GetPersonalModulePath()
        {
            string personalModuleRoot = Path.Combine(
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                            Utils.ProductNameForDirectory),
                    Utils.ModuleDirectory);

            return personalModuleRoot;
        }

        /// <summary>
        /// Gets the default system-wide module path.
        /// </summary>
        /// <returns>The default system wide module path</returns>
        internal static string GetSystemwideModulePath()
        {
            if (SystemWideModulePath != null)
                return SystemWideModulePath;

            // There is no runspace config so we use the default string
            string shellId = Utils.DefaultPowerShellShellID;

            // Now figure out what $PSHOME is.
            // This depends on the shellId. If we cannot read the application base
            // registry key, set the variable to empty string
            string psHome = null;
            try
            {
                psHome = Utils.GetApplicationBase(shellId);
            }
            catch (System.Security.SecurityException)
            {
            }

            if (!string.IsNullOrEmpty(psHome))
            {
                // Win8: 584267 Powershell Modules are listed twice in x86, and cannot be removed
                // This happens because ModuleTable uses Path as the key and CBS installer 
                // expands the path to include "SysWOW64" (for 
                // HKEY_LOCAL_MACHINE\SOFTWARE\Wow6432Node\Microsoft\PowerShell\3\PowerShellEngine ApplicationBase).
                // Because of this, the module that is getting loaded during startup (through LocalRunspace)
                // is using "SysWow64" in the key. Later, when Import-Module is called, it loads the 
                // module using ""System32" in the key.

                // Porting note: psHome cannot be lower-cased on case sensitive file systems
                if (Platform.IsWindows)
                {
                    psHome = psHome.ToLowerInvariant().Replace("\\syswow64\\", "\\system32\\");
                }
                Interlocked.CompareExchange(ref SystemWideModulePath, Path.Combine(psHome, Utils.ModuleDirectory), null);
            }

            return SystemWideModulePath;
        }

        private static string SystemWideModulePath;

        /// <summary>
        /// Get the DSC module path.
        /// </summary>
        /// <returns></returns>
        internal static string GetDscModulePath()
        {
            if (!Platform.IsWindows)
            {
                return string.Empty;
            }

            string dscModulePath = null;
            string programFilesPath = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(programFilesPath))
            {
                dscModulePath = Path.Combine(programFilesPath, Utils.DscModuleDirectory);
            }
            return dscModulePath;
        }

        /// <summary>
        /// Combine the PS system-wide module path and the DSC module path
        /// to get the system module paths.
        /// </summary>
        /// <returns></returns>
        private static string CombineSystemModulePaths()
        {
            string psSystemModulePath = GetSystemwideModulePath();
            string dscSystemModulePath = GetDscModulePath();

            if (psSystemModulePath != null && dscSystemModulePath != null)
            {
                return (dscSystemModulePath + ";" + psSystemModulePath);
            }

            if (psSystemModulePath != null || dscSystemModulePath != null)
            {
                return (psSystemModulePath ?? dscSystemModulePath);
            }

            return null;
        }

        private static string GetExpandedEnvironmentVariable(string name, EnvironmentVariableTarget target)
        {
            string result = Environment.GetEnvironmentVariable(name, target);
            if (!string.IsNullOrEmpty(result))
            {
                result = Environment.ExpandEnvironmentVariables(result);
            }
            return result;
        }

        /// <summary>
        /// Checks if a particular string (path) is a member of 'combined path' string (like %Path% or %PSModulePath%)
        /// </summary>
        /// <param name="pathToScan">'Combined path' string to analyze; can not be null.</param>
        /// <param name="pathToLookFor">Path to search for; can not be another 'combined path' (semicolon-separated); can not be null.</param>
        /// <returns>Index of pathToLookFor in pathToScan; -1 if not found.</returns>
        private static int PathContainsSubstring(string pathToScan, string pathToLookFor)
        {
            // we don't support if any of the args are null - parent function should ensure this; empty values are ok
            Diagnostics.Assert(pathToScan != null, "pathToScan should not be null according to contract of the function");
            Diagnostics.Assert(pathToLookFor != null, "pathToLookFor should not be null according to contract of the function");

            int pos = 0; // position of the current substring in pathToScan
            string[] substrings = pathToScan.Split(new char[] { ';' }, StringSplitOptions.None); // we want to process empty entries
            string goodPathToLookFor = pathToLookFor.Trim().TrimEnd('\\'); // trailing backslashes and white-spaces will mess up equality comparison
            foreach (string substring in substrings)
            {
                string goodSubstring = substring.Trim().TrimEnd('\\');  // trailing backslashes and white-spaces will mess up equality comparison

                // We have to use equality comparison on individual substrings (as opposed to simple 'string.IndexOf' or 'string.Contains')
                // because of cases like { pathToScan="C:\Temp\MyDir\MyModuleDir", pathToLookFor="C:\Temp" }
                if (string.Equals(goodSubstring, goodPathToLookFor, StringComparison.OrdinalIgnoreCase))
                {
                    return pos; // match found - return index of it in the 'pathToScan' string
                }
                else
                {
                    pos += substring.Length + 1; // '1' is for trailing semicolon
                }
            }
            // if we are here, that means a match was not found
            return -1;
        }

        /// <summary>
        /// Adds paths to a 'combined path' string (like %Path% or %PSModulePath%) if they are not already there.
        /// </summary>
        /// <param name="basePath">Path string (like %Path% or %PSModulePath%).</param>
        /// <param name="pathToAdd">Collection of individual paths to add.</param>
        /// <param name="insertPosition">-1 to append to the end; 0 to insert in the beginning of the string; etc...</param>
        /// <returns>Result string.</returns>
        private static string AddToPath(string basePath, string pathToAdd, int insertPosition)
        {
            // we don't support if any of the args are null - parent function should ensure this; empty values are ok
            Diagnostics.Assert(basePath != null, "basePath should not be null according to contract of the function");
            Diagnostics.Assert(pathToAdd != null, "pathToAdd should not be null according to contract of the function");

            System.Text.StringBuilder result = new System.Text.StringBuilder(basePath);

            char[] semicolonSeparator = new char[] { ';' };
            if (!string.IsNullOrEmpty(pathToAdd)) // we don't want to append empty paths
            {
                foreach (string subPathToAdd in pathToAdd.Split(semicolonSeparator, StringSplitOptions.RemoveEmptyEntries)) // in case pathToAdd is a 'combined path' (semicolon-separated)
                {
                    int position = PathContainsSubstring(result.ToString(), subPathToAdd); // searching in effective 'result' value ensures that possible duplicates in pathsToAdd are handled correctly
                    if (-1 == position) // subPathToAdd not found - add it
                    {
                        if (-1 == insertPosition) // append subPathToAdd to the end
                        {
                            bool resultHasEndingSemicolon = false;
                            if (result.Length > 0) resultHasEndingSemicolon = (result[result.Length - 1] == ';');

                            if (resultHasEndingSemicolon)
                                result.Append(subPathToAdd);
                            else
                                result.Append(";" + subPathToAdd);
                        }
                        else // insert at the requested location (this is used by DSC (<Program Files> location) and by 'user-specific location' (SpecialFolder.MyDocuments or EVT.User))
                        {
                            result.Insert(insertPosition, subPathToAdd + ";");
                        }
                    }
                }
            }

            return result.ToString();
        }

        /// <summary>
        /// Checks the various PSModulePath environment string and returns PSModulePath string as appropriate. Note - because these
        /// strings go through the provider, we need to escape any wildcards before passing them
        /// along.
        /// </summary>
        public static string GetModulePath(string currentProcessModulePath, string hklmMachineModulePath, string hkcuUserModulePath)
        {
            string programFilesModulePath = GetDscModulePath(); // aka <Program Files> location
            string psHomeModulePath = Environment.ExpandEnvironmentVariables(GetSystemwideModulePath()); // $PSHome\Modules location
            // If the variable isn't set, then set it to the default value
            if (currentProcessModulePath == null)  // EVT.Process does Not exist - really corner case
            {
                // Handle the default case...
                if (String.IsNullOrEmpty(hkcuUserModulePath)) // EVT.User does Not exist -> set to <SpecialFolder.MyDocuments> location
                {
                    currentProcessModulePath = GetPersonalModulePath(); // = SpecialFolder.MyDocuments + Utils.ProductNameForDirectory + Utils.ModuleDirectory
                }
                else // EVT.User exists -> set to EVT.User
                {
                    currentProcessModulePath = hkcuUserModulePath; // = EVT.User
                }

                currentProcessModulePath += ';';
                if (String.IsNullOrEmpty(hklmMachineModulePath)) // EVT.Machine does Not exist
                {
                    currentProcessModulePath += CombineSystemModulePaths(); // += (DscModulePath + $PSHome\Modules)
                }
                else
                {
                    currentProcessModulePath += hklmMachineModulePath; // += EVT.Machine
                }
            }
            else // EVT.Process exists
            {
                // Now handle the case where the environment variable is already set.

                // Porting note: Open PowerShell has a Modules folder in the the application base path which contains the built-in modules
                // It must be in the front of the path no matter what.
                if (Platform.IsCore)
                {
                    currentProcessModulePath = AddToPath(currentProcessModulePath, GetSystemwideModulePath(), 0);
                }

                // If there is no personal path key, then if the env variable doesn't match the system variable,
                // the user modified it somewhere, else prepend the default personel module path
                if (hklmMachineModulePath != null) // EVT.Machine exists
                {
                    if (hkcuUserModulePath == null) // EVT.User does Not exist
                    {
                        if (!(hklmMachineModulePath).Equals(currentProcessModulePath, StringComparison.OrdinalIgnoreCase))
                        {
                            // before returning, use <presence of Windows module path> heuristic to conditionally add programFilesModulePath
                            int psHomePosition = PathContainsSubstring(currentProcessModulePath, psHomeModulePath); // index of $PSHome\Modules in currentProcessModulePath
                            if (psHomePosition >= 0) // if $PSHome\Modules IS found - insert <Program Files> location before $PSHome\Modules
                            {
                                return AddToPath(currentProcessModulePath, programFilesModulePath, psHomePosition);
                            } // if $PSHome\Modules NOT found = <scenario 4> = 'PSModulePath has been constrained by a user to create a sand boxed environment without including System Modules'

                            return null;
                        }
                        currentProcessModulePath = GetPersonalModulePath() + ';' + hklmMachineModulePath; // <SpecialFolder.MyDocuments> + EVT.Machine + inserted <ProgramFiles> later in this function
                    }
                    else // EVT.User exists
                    {
                        // PSModulePath is designed to have behaviour like 'Path' var in a sense that EVT.User + EVT.Machine are merged to get final value of PSModulePath
                        string combined = string.Concat(hkcuUserModulePath, ';', hklmMachineModulePath); // EVT.User + EVT.Machine
                        if (!((combined).Equals(currentProcessModulePath, StringComparison.OrdinalIgnoreCase) ||
                            (hklmMachineModulePath).Equals(currentProcessModulePath, StringComparison.OrdinalIgnoreCase) ||
                            (hkcuUserModulePath).Equals(currentProcessModulePath, StringComparison.OrdinalIgnoreCase)))
                        {
                            // before returning, use <presence of Windows module path> heuristic to conditionally add programFilesModulePath
                            int psHomePosition = PathContainsSubstring(currentProcessModulePath, psHomeModulePath); // index of $PSHome\Modules in currentProcessModulePath
                            if (psHomePosition >= 0) // if $PSHome\Modules IS found - insert <Program Files> location before $PSHome\Modules
                            {
                                return AddToPath(currentProcessModulePath, programFilesModulePath, psHomePosition);
                            } // if $PSHome\Modules NOT found = <scenario 4> = 'PSModulePath has been constrained by a user to create a sand boxed environment without including System Modules'

                            return null;
                        }
                        currentProcessModulePath = combined; // = EVT.User + EVT.Machine + inserted <ProgramFiles> later in this function
                    }
                }
                else // EVT.Machine does Not exist
                {
                    // If there is no system path key, then if the env variable doesn't match the user variable,
                    // the user modified it somewhere, otherwise append the default system path
                    if (hkcuUserModulePath != null) // EVT.User exists
                    {
                        if (hkcuUserModulePath.Equals(currentProcessModulePath, StringComparison.OrdinalIgnoreCase))
                        {
                            currentProcessModulePath = hkcuUserModulePath + ';' + CombineSystemModulePaths(); // = EVT.User + (DscModulePath + $PSHome\Modules)
                        }
                        else
                        {
                            // before returning, use <presence of Windows module path> heuristic to conditionally add programFilesModulePath
                            int psHomePosition = PathContainsSubstring(currentProcessModulePath, psHomeModulePath); // index of $PSHome\Modules in currentProcessModulePath
                            if (psHomePosition >= 0) // if $PSHome\Modules IS found - insert <Program Files> location before $PSHome\Modules
                            {
                                return AddToPath(currentProcessModulePath, programFilesModulePath, psHomePosition);
                            } // if $PSHome\Modules NOT found = <scenario 4> = 'PSModulePath has been constrained by a user to create a sand boxed environment without including System Modules'

                            return null;
                        }
                    }
                    else // EVT.User does Not exist
                    {
                        // before returning, use <presence of Windows module path> heuristic to conditionally add programFilesModulePath
                        int psHomePosition = PathContainsSubstring(currentProcessModulePath, psHomeModulePath); // index of $PSHome\Modules in currentProcessModulePath
                        if (psHomePosition >= 0) // if $PSHome\Modules IS found - insert <Program Files> location before $PSHome\Modules
                        {
                            return AddToPath(currentProcessModulePath, programFilesModulePath, psHomePosition);
                        } // if $PSHome\Modules NOT found = <scenario 4> = 'PSModulePath has been constrained by a user to create a sand boxed environment without including System Modules'

                        // Neither key is set so go with what the environment variable is already set to
                        return null;
                    }
                }
            }

            // if we reached this point - always add <Program Files> location to EVT.Process
            // everything below is the same behaviour as WMF 4 code
            int indexOfPSHomeModulePath = PathContainsSubstring(currentProcessModulePath, psHomeModulePath); // index of $PSHome\Modules in currentProcessModulePath
            // if $PSHome\Modules not found (psHomePosition == -1) - append <Program Files> location to the end;
            // if $PSHome\Modules IS found (psHomePosition >= 0) - insert <Program Files> location before $PSHome\Modules
            currentProcessModulePath = AddToPath(currentProcessModulePath, programFilesModulePath, indexOfPSHomeModulePath);

            return currentProcessModulePath;
        }

        /// <summary>
        /// Checks if $env:PSModulePath is not set and sets it as appropriate. Note - because these
        /// strings go through the provider, we need to escape any wildcards before passing them
        /// along.
        /// </summary>
        internal static string GetModulePath()
        {
            string currentModulePath = GetExpandedEnvironmentVariable("PSMODULEPATH", EnvironmentVariableTarget.Process);
            return currentModulePath;
        }
        /// <summary>
        /// Checks if $env:PSModulePath is not set and sets it as appropriate. Note - because these
        /// strings go through the provider, we need to escape any wildcards before passing them
        /// along.
        /// </summary>
        internal static string SetModulePath()
        {
            string currentModulePath = GetExpandedEnvironmentVariable("PSMODULEPATH", EnvironmentVariableTarget.Process);
            string systemWideModulePath = GetExpandedEnvironmentVariable("PSMODULEPATH", EnvironmentVariableTarget.Machine);
            string personalModulePath = GetExpandedEnvironmentVariable("PSMODULEPATH", EnvironmentVariableTarget.User);

            string newModulePathString = GetModulePath(currentModulePath, systemWideModulePath, personalModulePath);

            if(!string.IsNullOrEmpty(newModulePathString))
            {
                // Set the environment variable...
                Environment.SetEnvironmentVariable("PSMODULEPATH", newModulePathString);
            }

            return newModulePathString;
        }

        /// <summary>
        /// Get the current module path setting.
        /// 
        /// 'preferSystemModulePath' should only be used for internal functions - by default,
        /// user modules should be able to override system modules.
        /// </summary>
        /// <returns>The module path as an array of strings</returns>
        internal static IEnumerable<string> GetModulePath(bool preferSystemModulePath, ExecutionContext context)
        {
            string modulePathString = Environment.GetEnvironmentVariable("PSMODULEPATH") ?? SetModulePath();

            HashSet<string> processedPathSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // If we should prefer the system-wide module path, prepend that to what gets returned.
            if (preferSystemModulePath)
            {
                yield return ProcessOneModulePath(context, GetSystemwideModulePath(), processedPathSet);
            }

            // If the path is just whitespace, return an empty collection.
            if (string.IsNullOrWhiteSpace(modulePathString))
                yield break;

            foreach (string envPath in modulePathString.Split(Utils.Separators.Semicolon, StringSplitOptions.RemoveEmptyEntries))
            {
                var processedPath = ProcessOneModulePath(context, envPath, processedPathSet);
                if (processedPath != null)
                    yield return processedPath;
            }
        }

        static private string ProcessOneModulePath(ExecutionContext context, string envPath, HashSet<string> processedPathSet)
        {
            string trimmedenvPath = envPath.Trim();

            bool isUnc = Utils.PathIsUnc(trimmedenvPath);
            if (!isUnc)
            {
                // if the path start with "filesystem::", remove it so we can test for URI and
                // also Directory.Exists (if the file system provider isn't actually loaded.)
                if (trimmedenvPath.StartsWith("filesystem::", StringComparison.OrdinalIgnoreCase))
                {
                    trimmedenvPath = trimmedenvPath.Remove(0, 12 /*"filesystem::".Length*/);
                }

                isUnc = Utils.PathIsUnc(trimmedenvPath);
            }

            // If we have an unc, just return the value as resolving the path is expensive.
            if (isUnc)
            {
                return trimmedenvPath;
            }

            // We prefer using the file system provider to resolve paths so callers can avoid processing
            // duplicates, e.g. the following are all the same:
            //     a\b
            //     a\.\b
            //     a\b\
            // But if the file system provider isn't loaded, we will just check if the directory exists.
            if (context.EngineSessionState.IsProviderLoaded(context.ProviderNames.FileSystem))
            {
                ProviderInfo provider = null;
                IEnumerable<string> resolvedPaths = null;
                try
                {
                    resolvedPaths = context.SessionState.Path.GetResolvedProviderPathFromPSPath(
                        WildcardPattern.Escape(trimmedenvPath), out provider);
                }
                catch (ItemNotFoundException)
                {
                    // silently skip directories that are not found
                }
                catch (DriveNotFoundException)
                {
                    // silently skip drives that are not found
                }
                catch (NotSupportedException)
                {
                    // silently skip invalid path
                    // NotSupportedException is thrown if path contains a colon (":") that is not part of a
                    // volume identifier (for example, "c:\" is Supported but not "c:\temp\Z:\invalidPath")
                }

                if (provider != null && resolvedPaths != null && provider.NameEquals(context.ProviderNames.FileSystem))
                {
                    var result = resolvedPaths.FirstOrDefault();
                    if (processedPathSet.Add(result))
                    {
                        return result;
                    }
                }
            }
            else if (Directory.Exists(trimmedenvPath))
            {
                return trimmedenvPath;
            }

            return null;
        }

        static private void SortAndRemoveDuplicates<T>(List<T> input, Func<T, string> keyGetter)
        {
            Dbg.Assert(input != null, "Caller should verify that input != null");

            input.Sort(
                delegate(T x, T y)
                {
                    string kx = keyGetter(x);
                    string ky = keyGetter(y);
                    return string.Compare(kx, ky, StringComparison.OrdinalIgnoreCase);
                }
            );

            bool firstItem = true;
            string previousKey = null;
            List<T> output = new List<T>(input.Count);
            foreach (T item in input)
            {
                string currentKey = keyGetter(item);
                if ((firstItem) || !currentKey.Equals(previousKey, StringComparison.OrdinalIgnoreCase))
                {
                    output.Add(item);
                }

                previousKey = currentKey;
                firstItem = false;
            }

            input.Clear();
            input.AddRange(output);
        }

        /// <summary>
        /// Mark stuff to be exported from the current environment using the various patterns
        /// </summary>
        /// <param name="cmdlet">The cmdlet calling this method</param>
        /// <param name="sessionState">The session state instance to do the exports on</param>
        /// <param name="functionPatterns">Patterns describing the functions to export</param>
        /// <param name="cmdletPatterns">Patterns describing the cmdlets to export</param>
        /// <param name="aliasPatterns">Patterns describing the aliases to export</param>
        /// <param name="variablePatterns">Patterns describing the variables to export</param>
        /// <param name="doNotExportCmdlets">List of Cmdlets that will not be exported,  
        ///     even if they match in cmdletPatterns.</param>
        static internal void ExportModuleMembers(PSCmdlet cmdlet, SessionStateInternal sessionState,
            List<WildcardPattern> functionPatterns, List<WildcardPattern> cmdletPatterns,
            List<WildcardPattern> aliasPatterns, List<WildcardPattern> variablePatterns, List<string> doNotExportCmdlets)
        {
            // If this cmdlet is called, then mark that the export list should be used for exporting
            // module members...

            sessionState.UseExportList = true;

            if (functionPatterns != null)
            {
                IDictionary<string, FunctionInfo> ft = sessionState.ModuleScope.FunctionTable;

                foreach (KeyValuePair<string, FunctionInfo> entry in ft)
                {
                    // Skip AllScope functions
                    if ((entry.Value.Options & ScopedItemOptions.AllScope) != 0)
                    {
                        continue;
                    }

                    if (SessionStateUtilities.MatchesAnyWildcardPattern(entry.Key, functionPatterns, false))
                    {
                        string message;

                        if (entry.Value.CommandType == CommandTypes.Workflow)
                        {
                            message = StringUtil.Format(Modules.ExportingWorkflow, entry.Key);
                            sessionState.ExportedWorkflows.Add((WorkflowInfo)entry.Value);
                        }
                        else
                        {
                            message = StringUtil.Format(Modules.ExportingFunction, entry.Key);
	                        sessionState.ExportedFunctions.Add(entry.Value);
                        }

                        cmdlet.WriteVerbose(message);
                    }
                }
                SortAndRemoveDuplicates(sessionState.ExportedFunctions, delegate(FunctionInfo ci) { return ci.Name; });
                SortAndRemoveDuplicates(sessionState.ExportedWorkflows, delegate(WorkflowInfo ci) { return ci.Name; });
            }

            if (cmdletPatterns != null)
            {
                IDictionary<string, List<CmdletInfo>> ft = sessionState.ModuleScope.CmdletTable;

                // Subset the existing cmdlet exports if there are any. This will be the case
                // if we're using ModuleToProcess to import a binary module which has nested modules.
                if (sessionState.Module.CompiledExports.Count > 0)
                {
                    CmdletInfo[] copy = sessionState.Module.CompiledExports.ToArray();
                    sessionState.Module.CompiledExports.Clear();

                    foreach (CmdletInfo element in copy)
                    {
                        if (doNotExportCmdlets == null
                            || !doNotExportCmdlets.Exists(cmdletName => string.Equals(element.FullName, cmdletName, StringComparison.OrdinalIgnoreCase)))
                        {
                            if (SessionStateUtilities.MatchesAnyWildcardPattern(element.Name, cmdletPatterns, false))
                            {
                                string message = StringUtil.Format(Modules.ExportingCmdlet, element.Name);
                                cmdlet.WriteVerbose(message);
                                // Copy the cmdlet info, changing the module association to be the current module...
                                CmdletInfo exportedCmdlet = new CmdletInfo(element.Name, element.ImplementingType,
                                    element.HelpFile, null, element.Context) {Module = sessionState.Module};
                                Dbg.Assert(sessionState.Module != null, "sessionState.Module should not be null by the time we're exporting cmdlets");
                                sessionState.Module.CompiledExports.Add(exportedCmdlet);
                            }
                        }
                    }
                }

                // And copy in any cmdlets imported from the nested modules...
                foreach (KeyValuePair<string, List<CmdletInfo>> entry in ft)
                {
                    CmdletInfo cmdletToImport = entry.Value[0];
                    if (doNotExportCmdlets == null
                        || !doNotExportCmdlets.Exists(cmdletName => string.Equals(cmdletToImport.FullName, cmdletName, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (SessionStateUtilities.MatchesAnyWildcardPattern(entry.Key, cmdletPatterns, false))
                        {
                            string message = StringUtil.Format(Modules.ExportingCmdlet, entry.Key);
                            cmdlet.WriteVerbose(message);
                            // Copy the cmdlet info, changing the module association to be the current module...
                            CmdletInfo exportedCmdlet = new CmdletInfo(cmdletToImport.Name, cmdletToImport.ImplementingType,
                                cmdletToImport.HelpFile, null, cmdletToImport.Context) {Module = sessionState.Module};
                            Dbg.Assert(sessionState.Module != null, "sessionState.Module should not be null by the time we're exporting cmdlets");
                            sessionState.Module.CompiledExports.Add(exportedCmdlet);
                        }
                    }
                }

                SortAndRemoveDuplicates(sessionState.Module.CompiledExports, delegate(CmdletInfo ci) { return ci.Name; });
            }

            if (variablePatterns != null)
            {
                IDictionary<string, PSVariable> vt = sessionState.ModuleScope.Variables;

                foreach (KeyValuePair<string, PSVariable> entry in vt)
                {

                    // The magic variables are always private as are all-scope variables...
                    if (entry.Value.IsAllScope || Array.IndexOf(PSModuleInfo._builtinVariables, entry.Key) != -1)
                    {
                        continue;
                    }

                    if (SessionStateUtilities.MatchesAnyWildcardPattern(entry.Key, variablePatterns, false))
                    {
                        string message = StringUtil.Format(Modules.ExportingVariable, entry.Key);
                        cmdlet.WriteVerbose(message);
                        sessionState.ExportedVariables.Add(entry.Value);
                    }
                }
                SortAndRemoveDuplicates(sessionState.ExportedVariables, delegate(PSVariable v) { return v.Name; });
            }

            if (aliasPatterns != null)
            {
                IEnumerable<AliasInfo> mai = sessionState.ModuleScope.AliasTable;

                // Subset the existing alias exports if there are any. This will be the case
                // if we're using ModuleToProcess to import a binary module which has nested modules.
                if (sessionState.Module.CompiledAliasExports.Count > 0)
                {
                    AliasInfo[] copy = sessionState.Module.CompiledAliasExports.ToArray();

                    foreach (var element in copy)
                    {
                        if (SessionStateUtilities.MatchesAnyWildcardPattern(element.Name, aliasPatterns, false))
                        {
                            string message = StringUtil.Format(Modules.ExportingAlias, element.Name);
                            cmdlet.WriteVerbose(message);
                            sessionState.ExportedAliases.Add(NewAliasInfo(element, sessionState));
                        }
                    }
                }

                foreach (AliasInfo entry in mai)
                {
                    // Skip allscope items...
                    if ((entry.Options & ScopedItemOptions.AllScope) != 0)
                    {
                        continue;
                    }

                    if (SessionStateUtilities.MatchesAnyWildcardPattern(entry.Name, aliasPatterns, false))
                    {
                        string message = StringUtil.Format(Modules.ExportingAlias, entry.Name);
                        cmdlet.WriteVerbose(message);
                        sessionState.ExportedAliases.Add(NewAliasInfo(entry, sessionState));
                    }
                }

                SortAndRemoveDuplicates(sessionState.ExportedAliases, delegate(AliasInfo ci) { return ci.Name; });
            }
        }

        static private AliasInfo NewAliasInfo(AliasInfo alias, SessionStateInternal sessionState)
        {
            Dbg.Assert(alias != null, "alias should not be null");
            Dbg.Assert(sessionState != null, "sessionState should not be null");
            Dbg.Assert(sessionState.Module != null, "sessionState.Module should not be null by the time we're exporting aliases");

            // Copy the alias info, changing the module association to be the current module...
            var aliasCopy = new AliasInfo(alias.Name, alias.Definition, alias.Context, alias.Options)
            {
                Module = sessionState.Module
            };
            return aliasCopy;
        }
    } // ModuleIntrinsics

    /// <summary>
    /// Used by Modules/Snapins to provide a hook to the engine for startup initialization
    /// w.r.t compiled assembly loading.
    /// </summary>
    public interface IModuleAssemblyInitializer
    {
        /// <summary>
        /// Gets called when assembly is loaded.
        /// </summary>
        void OnImport();
    }

    /// <summary>
    /// Used by modules to provide a hooko to the engine for cleanup on removal
    /// w.r.t. compiled assembly being removed.
    /// </summary>
    public interface IModuleAssemblyCleanup
    {
        /// <summary>
        /// Gets called when the binary module is unloaded.
        /// </summary>
        void OnRemove(PSModuleInfo psModuleInfo);
    }

} // System.Management.Automation