﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.PackageManager;
using UnityEditor.Scripting.Compilers;
using UnityEditor.VisualStudioIntegration;
using UnityEngine;

namespace VSCodePackage
{
    public class ProjectGeneration
    {
        internal enum ScriptingLanguage
        {
            None,
            CSharp
        }
        enum Mode
        {
            UnityScriptAsUnityProj,
            UnityScriptAsPrecompiledAssembly,
        }

        public static readonly string MSBuildNamespaceUri = "http://schemas.microsoft.com/developer/msbuild/2003";

        const string k_WindowsNewline = "\r\n";

        const string k_SettingsJson = @"{
    ""files.exclude"":
    {
        ""**/.DS_Store"":true,
        ""**/.git"":true,
        ""**/.gitignore"":true,
        ""**/.gitmodules"":true,
        ""**/*.booproj"":true,
        ""**/*.pidb"":true,
        ""**/*.suo"":true,
        ""**/*.user"":true,
        ""**/*.userprefs"":true,
        ""**/*.unityproj"":true,
        ""**/*.dll"":true,
        ""**/*.exe"":true,
        ""**/*.pdf"":true,
        ""**/*.mid"":true,
        ""**/*.midi"":true,
        ""**/*.wav"":true,
        ""**/*.gif"":true,
        ""**/*.ico"":true,
        ""**/*.jpg"":true,
        ""**/*.jpeg"":true,
        ""**/*.png"":true,
        ""**/*.psd"":true,
        ""**/*.tga"":true,
        ""**/*.tif"":true,
        ""**/*.tiff"":true,
        ""**/*.3ds"":true,
        ""**/*.3DS"":true,
        ""**/*.fbx"":true,
        ""**/*.FBX"":true,
        ""**/*.lxo"":true,
        ""**/*.LXO"":true,
        ""**/*.ma"":true,
        ""**/*.MA"":true,
        ""**/*.obj"":true,
        ""**/*.OBJ"":true,
        ""**/*.asset"":true,
        ""**/*.cubemap"":true,
        ""**/*.flare"":true,
        ""**/*.mat"":true,
        ""**/*.meta"":true,
        ""**/*.prefab"":true,
        ""**/*.unity"":true,
        ""build/"":true,
        ""Build/"":true,
        ""Library/"":true,
        ""library/"":true,
        ""obj/"":true,
        ""Obj/"":true,
        ""ProjectSettings/"":true,
        ""temp/"":true,
        ""Temp/"":true
    }
}";

        /// <summary>
        /// Map source extensions to ScriptingLanguages
        /// </summary>
        internal static readonly Dictionary<string, ScriptingLanguage> BuiltinSupportedExtensions = new Dictionary<string, ScriptingLanguage>
        {
            { "cs", ScriptingLanguage.CSharp },
            { "uxml", ScriptingLanguage.None },
            { "uss", ScriptingLanguage.None },
            { "shader", ScriptingLanguage.None },
            { "compute", ScriptingLanguage.None },
            { "cginc", ScriptingLanguage.None },
            { "hlsl", ScriptingLanguage.None },
            { "glslinc", ScriptingLanguage.None },
        };

        string solutionProjectEntryTemplate = string.Join("\r\n", new[]
        {
            @"Project(""{{{0}}}"") = ""{1}"", ""{2}"", ""{{{3}}}""",
            @"EndProject"
        }).Replace("    ", "\t");

        string solutionProjectConfigurationTemplate = string.Join("\r\n", new[]
        {
            @"        {{{0}}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU",
            @"        {{{0}}}.Debug|Any CPU.Build.0 = Debug|Any CPU",
            @"        {{{0}}}.Release|Any CPU.ActiveCfg = Release|Any CPU",
            @"        {{{0}}}.Release|Any CPU.Build.0 = Release|Any CPU"
        }).Replace("    ", "\t");

        static readonly string[] k_ReimportSyncExtensions = { ".dll", ".asmdef" };

        /// <summary>
        /// Map ScriptingLanguages to project extensions
        /// </summary>
        /*static readonly Dictionary<ScriptingLanguage, string> k_ProjectExtensions = new Dictionary<ScriptingLanguage, string>
        {
            { ScriptingLanguage.CSharp, ".csproj" },
            { ScriptingLanguage.None, ".csproj" },
        };*/

        static readonly Regex k_MonoDevelopPropertyHeader = new Regex(@"^\s*GlobalSection\(MonoDevelopProperties.*\)");
        static readonly Regex k_ScriptReferenceExpression = new Regex(
            @"^Library.ScriptAssemblies.(?<dllname>(?<project>.*)\.dll$)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        string[] m_ProjectSupportedExtensions = new string[0];
        public string ProjectDirectory { get; private set; }
        readonly string m_ProjectName;

        static readonly string k_DefaultMonoDevelopSolutionProperties = string.Join("\r\n", new[]
        {
            "    GlobalSection(MonoDevelopProperties) = preSolution",
            "        StartupItem = Assembly-CSharp.csproj",
            "    EndGlobalSection",
        }).Replace("    ", "\t");

        public ProjectGeneration()
        {
            var projectDirectory = Directory.GetParent(Application.dataPath).FullName;
            ProjectDirectory = projectDirectory.Replace('\\', '/');
            m_ProjectName = Path.GetFileName(ProjectDirectory);
        }

        /// <summary>
        /// Syncs the scripting solution if any affected files are relevant.
        /// </summary>
        /// <returns>
        /// Whether the solution was synced.
        /// </returns>
        /// <param name='affectedFiles'>
        /// A set of files whose status has changed
        /// </param>
        /// <param name="reimportedFiles">
        /// A set of files that got reimported
        /// </param>
        public void SyncIfNeeded(IEnumerable<string> affectedFiles, IEnumerable<string> reimportedFiles)
        {
            SetupProjectSupportedExtensions();

            // Don't sync if we haven't synced before
            if (HasSolutionBeenGenerated() && HasFilesBeenModified(affectedFiles, reimportedFiles))
            {
                Sync();
            }
        }

        bool HasFilesBeenModified(IEnumerable<string> affectedFiles, IEnumerable<string> reimportedFiles)
        {
            return affectedFiles.Any(ShouldFileBePartOfSolution) || reimportedFiles.Any(ShouldSyncOnReimportedAsset);
        }

        static bool ShouldSyncOnReimportedAsset(string asset)
        {
            return k_ReimportSyncExtensions.Contains(Path.GetExtension(asset));
        }

        public void Sync()
        {
            SetupProjectSupportedExtensions();
            GenerateAndWriteSolutionAndProjects();
        }

        bool HasSolutionBeenGenerated()
        {
            return File.Exists(SolutionFile());
        }

        void SetupProjectSupportedExtensions()
        {
            m_ProjectSupportedExtensions = EditorSettings.projectGenerationUserExtensions;
        }

        bool ShouldFileBePartOfSolution(string file)
        {
            string extension = Path.GetExtension(file);

            // Exclude files coming from packages except if they are internalized.
            if (IsNonInternalizedPackagePath(file))
            {
                return false;
            }

            // Dll's are not scripts but still need to be included..
            if (extension == ".dll")
                return true;

            if (file.ToLower().EndsWith(".asmdef"))
                return true;

            return IsSupportedExtension(extension);
        }

        bool IsSupportedExtension(string extension)
        {
            extension = extension.TrimStart('.');
            if (BuiltinSupportedExtensions.ContainsKey(extension))
                return true;
            if (m_ProjectSupportedExtensions.Contains(extension))
                return true;
            return false;
        }

        static ScriptingLanguage ScriptingLanguageFor(Assembly island)
        {
            return ScriptingLanguageFor(GetExtensionOfSourceFiles(island.sourceFiles));
        }

        static string GetExtensionOfSourceFiles(string[] files)
        {
            return files.Length > 0 ? GetExtensionOfSourceFile(files[0]) : "NA";
        }

        static string GetExtensionOfSourceFile(string file)
        {
            var ext = Path.GetExtension(file).ToLower();
            ext = ext.Substring(1); //strip dot
            return ext;
        }

        static ScriptingLanguage ScriptingLanguageFor(string extension)
        {
            ScriptingLanguage result;
            if (BuiltinSupportedExtensions.TryGetValue(extension.TrimStart('.'), out result))
                return result;

            return ScriptingLanguage.None;
        }

        bool ProjectExists(Assembly island)
        {
            return File.Exists(ProjectFile(island));
        }

        void GenerateAndWriteSolutionAndProjects()
        {
            // Only synchronize islands that have associated source files and ones that we actually want in the project.
            // This also filters out DLLs coming from .asmdef files in packages.
            var assemblies = CompilationPipeline.GetAssemblies().
                Where(i => 0 < i.sourceFiles.Length && i.sourceFiles.Any(ShouldFileBePartOfSolution));

            var allAssetProjectParts = GenerateAllAssetProjectParts();

            var monoIslands = assemblies.ToList();


            SyncSolution(monoIslands);
            var allProjectIslands = RelevantIslandsForMode(monoIslands, ModeForCurrentExternalEditor()).ToList();
            foreach (Assembly assembly in allProjectIslands)
            {
                var responseFileData = ParseResponseFileData(assembly);
                SyncProject(assembly, allAssetProjectParts, responseFileData, allProjectIslands);
            }

            WriteVSCodeSettingsFiles();
        }

        IEnumerable<ResponseFileData> ParseResponseFileData(Assembly assembly)
        {
            var systemReferenceDirectories = CompilationPipeline.GetSystemReferenceDirectories(assembly.apiCompatibilityLevel);

            Dictionary<string, ResponseFileData> responseFilesData = assembly.responseFiles.ToDictionary(x => x, x => CompilationPipeline.ResolveResponseFile(
                Path.Combine(ProjectDirectory, x),
                ProjectDirectory,
                systemReferenceDirectories
            ));

            Dictionary<string, ResponseFileData> responseFilesWithErrors = responseFilesData.Where(x => x.Value.Errors.Any())
                .ToDictionary(x => x.Key, x => x.Value);

            if (responseFilesWithErrors.Any())
            {
                foreach (var error in responseFilesWithErrors)
                foreach (var valueError in error.Value.Errors)
                {
                    UnityEngine.Debug.LogErrorFormat("{0} Parse Error : {1}", error.Key, valueError);
                }
            }

            return responseFilesData.Select(x => x.Value);
        }

        Dictionary<string, string> GenerateAllAssetProjectParts()
        {
            Dictionary<string, StringBuilder> stringBuilders = new Dictionary<string, StringBuilder>();

            foreach (string asset in AssetDatabase.GetAllAssetPaths())
            {
                // Exclude files coming from packages except if they are internalized.
                // TODO: We need assets from the assembly API
                if (IsNonInternalizedPackagePath(asset))
                {
                    continue;
                }

                string extension = Path.GetExtension(asset);
                if (IsSupportedExtension(extension) && ScriptingLanguage.None == ScriptingLanguageFor(extension))
                {
                    // Find assembly the asset belongs to by adding script extension and using compilation pipeline.
                    var assemblyName = CompilationPipeline.GetAssemblyNameFromScriptPath(asset + ".cs");
                    assemblyName = assemblyName ?? CompilationPipeline.GetAssemblyNameFromScriptPath(asset + ".js");
                    assemblyName = assemblyName ?? CompilationPipeline.GetAssemblyNameFromScriptPath(asset + ".boo");

                    assemblyName = Path.GetFileNameWithoutExtension(assemblyName);

                    StringBuilder projectBuilder;

                    if (!stringBuilders.TryGetValue(assemblyName, out projectBuilder))
                    {
                        projectBuilder = new StringBuilder();
                        stringBuilders[assemblyName] = projectBuilder;
                    }

                    projectBuilder.AppendFormat("     <None Include=\"{0}\" />{1}", EscapedRelativePathFor(asset), k_WindowsNewline);
                }
            }

            var result = new Dictionary<string, string>();

            foreach (var entry in stringBuilders)
                result[entry.Key] = entry.Value.ToString();

            return result;
        }

        static bool IsNonInternalizedPackagePath(string file)
        {
            var packageInfo = CompilationPipeline.PackageSourceForAssetPath(file);
            return packageInfo == PackageSource.Embedded || packageInfo == PackageSource.Local;
        }

        void SyncProject(Assembly island,
            Dictionary<string, string> allAssetsProjectParts,
            IEnumerable<ResponseFileData> responseFilesData,
            List<Assembly> allProjectIslands)
        {
            SyncProjectFileIfNotChanged(ProjectFile(island), ProjectText(island, ModeForCurrentExternalEditor(), allAssetsProjectParts, responseFilesData, allProjectIslands));
        }

        static void SyncProjectFileIfNotChanged(string path, string newContents)
        {
            if (Path.GetExtension(path) == ".csproj")
            {
                //newContents = AssetPostprocessingInternal.CallOnGeneratedCSProject(path, newContents); TODO: Call specific code here
            }

            SyncFileIfNotChanged(path, newContents);
        }

        static void SyncSolutionFileIfNotChanged(string path, string newContents)
        {
            //newContents = AssetPostprocessingInternal.CallOnGeneratedSlnSolution(path, newContents); TODO: Call specific code here

            SyncFileIfNotChanged(path, newContents);
        }

        static void SyncFileIfNotChanged(string filename, string newContents)
        {
            if (File.Exists(filename) &&
                newContents == File.ReadAllText(filename))
            {
                return;
            }

            File.WriteAllText(filename, newContents, Encoding.UTF8);
        }

        string ProjectText(Assembly assembly,
            Mode mode,
            Dictionary<string, string> allAssetsProjectParts,
            IEnumerable<ResponseFileData> responseFilesData,
            List<Assembly> allProjectIslands)
        {
            var projectBuilder = new StringBuilder(ProjectHeader(assembly, responseFilesData));
            var references = new List<string>();
            var projectReferences = new List<Match>();
            Match match;
            bool isBuildingEditorProject = assembly.outputPath.EndsWith("-Editor.dll");

            foreach (string file in assembly.sourceFiles)
            {
                if (!ShouldFileBePartOfSolution(file))
                    continue;

                var extension = Path.GetExtension(file).ToLower();
                var fullFile = EscapedRelativePathFor(file);
                if (".dll" != extension)
                {
                    var tagName = "Compile";
                    projectBuilder.AppendFormat("     <{0} Include=\"{1}\" />{2}", tagName, fullFile, k_WindowsNewline);
                }
                else
                {
                    references.Add(fullFile);
                }
            }

            string additionalAssetsForProject;
            var assemblyName = Path.GetFileNameWithoutExtension(assembly.outputPath);

            // Append additional non-script files that should be included in project generation.
            if (allAssetsProjectParts.TryGetValue(assemblyName, out additionalAssetsForProject))
                projectBuilder.Append(additionalAssetsForProject);

            var allAdditionalReferenceFilenames = new List<string>();
            var islandRefs = references.Union(assembly.allReferences);

            foreach (string reference in islandRefs)
            {
                if (reference.EndsWith("/UnityEditor.dll") || reference.EndsWith("/UnityEngine.dll") || reference.EndsWith("\\UnityEditor.dll") || reference.EndsWith("\\UnityEngine.dll"))
                    continue;

                match = k_ScriptReferenceExpression.Match(reference);
                if (match.Success)
                {
                    // assume csharp language
                    // Add a reference to a project except if it's a reference to a script assembly
                    // that we are not generating a project for. This will be the case for assemblies
                    // coming from .assembly.json files in non-internalized packages.
                    var dllName = match.Groups["dllname"].Value;
                    if (allProjectIslands.Any(i => Path.GetFileName(i.outputPath) == dllName))
                    {
                        projectReferences.Add(match);
                        continue;
                    }
                }

                string fullReference = Path.IsPathRooted(reference) ? reference : Path.Combine(ProjectDirectory, reference);
                if (CompilationPipeline.IsInternalAssembly(fullReference, isBuildingEditorProject, allAdditionalReferenceFilenames))
                    continue;

                AppendReference(fullReference, projectBuilder);
            }

            var responseRefs = responseFilesData.SelectMany(x => x.FullPathReferences.Select(r => r));
            foreach (var reference in responseRefs)
            {
                AppendReference(reference, projectBuilder);
            }

            if (0 < projectReferences.Count)
            {
                string referencedProject;
                projectBuilder.AppendLine("  </ItemGroup>");
                projectBuilder.AppendLine("  <ItemGroup>");
                foreach (Match reference in projectReferences)
                {
//                    ScriptingLanguage targetLanguage = ScriptingLanguage.CSharp; // Assume CSharp
//                    var targetAssembly = EditorCompilationInterface.Instance.GetTargetAssemblyDetails(reference.Groups["dllname"].Value);
//                    ScriptingLanguage targetLanguage = ScriptingLanguage.None;
//                    if (targetAssembly != null)
//                        targetLanguage = (ScriptingLanguage)Enum.Parse(typeof(ScriptingLanguage), targetAssembly.Language.GetLanguageName(), true);
                    referencedProject = reference.Groups["project"].Value;
                    projectBuilder.AppendFormat("    <ProjectReference Include=\"{0}{1}\">{2}", referencedProject,
                        GetProjectExtension(), k_WindowsNewline);
                    projectBuilder.AppendFormat("      <Project>{{{0}}}</Project>", ProjectGuid(Path.Combine("Temp", reference.Groups["project"].Value + ".dll")), k_WindowsNewline);
                    projectBuilder.AppendFormat("      <Name>{0}</Name>", referencedProject, k_WindowsNewline);
                    projectBuilder.AppendLine("    </ProjectReference>");
                }
            }

            projectBuilder.Append(ProjectFooter(assembly));
            return projectBuilder.ToString();
        }

        static void AppendReference(string fullReference, StringBuilder projectBuilder)
        {
            //replace \ with / and \\ with /
            var escapedFullPath = SecurityElement.Escape(fullReference);
            escapedFullPath = escapedFullPath.Replace("\\", "/");
            escapedFullPath = escapedFullPath.Replace("\\\\", "/");
            projectBuilder.AppendFormat(" <Reference Include=\"{0}\">{1}", Path.GetFileNameWithoutExtension(escapedFullPath), k_WindowsNewline);
            projectBuilder.AppendFormat(" <HintPath>{0}</HintPath>{1}", escapedFullPath, k_WindowsNewline);
            projectBuilder.AppendFormat(" </Reference>{0}", k_WindowsNewline);
        }

        string ProjectFile(Assembly island)
        {
            return Path.Combine(ProjectDirectory, $"{Path.GetFileNameWithoutExtension(island.outputPath)}.csproj");
        }

        public string SolutionFile()
        {
            return Path.Combine(ProjectDirectory, $"{m_ProjectName}.sln");
        }

        string ProjectHeader(
            Assembly island,
            IEnumerable<ResponseFileData> responseFilesData
        )
        {
            string targetFrameworkVersion;
            string targetLanguageVersion;
            var toolsVersion = "4.0";
            var productVersion = "10.0.20506";
            const string baseDirectory = ".";

            if (island.apiCompatibilityLevel == ApiCompatibilityLevel.NET_4_6)
            {
                targetFrameworkVersion = "v4.7.2";
                targetLanguageVersion = "latest";
            }
            else
            {
                targetFrameworkVersion = "v3.5";
                targetLanguageVersion = "4";
            }

            var arguments = new object[]
            {
                toolsVersion, productVersion, ProjectGuid(island.outputPath),
                UnityEditorInternal.InternalEditorUtility.GetEngineAssemblyPath(),
                UnityEditorInternal.InternalEditorUtility.GetEditorAssemblyPath(),
                string.Join(";", new[] { "DEBUG", "TRACE" }.Concat(EditorUserBuildSettings.activeScriptCompilationDefines).Concat(island.defines).Concat(responseFilesData.SelectMany(x => x.Defines)).Distinct().ToArray()),
                MSBuildNamespaceUri,
                Path.GetFileNameWithoutExtension(island.outputPath),
                EditorSettings.projectGenerationRootNamespace,
                targetFrameworkVersion,
                targetLanguageVersion,
                baseDirectory,
                island.allowUnsafeCode | responseFilesData.Any(x => x.Unsafe)
            };

            try
            {
                return string.Format(GetProjectHeaderTemplate(), arguments);
            }
            catch (Exception)
            {
                throw new NotSupportedException("Failed creating c# project because the c# project header did not have the correct amount of arguments, which is " + arguments.Length);
            }
        }

        string GetSolutionText()
        {
            return string.Join("\r\n", new[]
            {
                @"",
                @"Microsoft Visual Studio Solution File, Format Version {0}",
                @"# Visual Studio {1}",
                @"{2}",
                @"Global",
                @"    GlobalSection(SolutionConfigurationPlatforms) = preSolution",
                @"        Debug|Any CPU = Debug|Any CPU",
                @"        Release|Any CPU = Release|Any CPU",
                @"    EndGlobalSection",
                @"    GlobalSection(ProjectConfigurationPlatforms) = postSolution",
                @"{3}",
                @"    EndGlobalSection",
                @"    GlobalSection(SolutionProperties) = preSolution",
                @"        HideSolutionNode = FALSE",
                @"    EndGlobalSection",
                @"{4}",
                @"EndGlobal",
                @""
            }).Replace("    ", "\t");
        }

        string GetProjectFooterTemplate()
        {
            return string.Join("\r\n", new[]
            {
                @"  </ItemGroup>",
                @"  <Import Project=""$(MSBuildToolsPath)\Microsoft.CSharp.targets"" />",
                @"  <!-- To modify your build process, add your task inside one of the targets below and uncomment it. ",
                @"       Other similar extension points exist, see Microsoft.Common.targets.",
                @"  <Target Name=""BeforeBuild"">",
                @"  </Target>",
                @"  <Target Name=""AfterBuild"">",
                @"  </Target>",
                @"  -->",
                @"  {0}",
                @"</Project>",
                @""
            });
        }

        string GetProjectHeaderTemplate()
        {
            var header = new[]
            {
                @"<?xml version=""1.0"" encoding=""utf-8""?>",
                @"<Project ToolsVersion=""{0}"" DefaultTargets=""Build"" xmlns=""{6}"">",
                @"  <PropertyGroup>",
                @"    <LangVersion>{10}</LangVersion>",
                @"  </PropertyGroup>",
                @"  <PropertyGroup>",
                @"    <Configuration Condition="" '$(Configuration)' == '' "">Debug</Configuration>",
                @"    <Platform Condition="" '$(Platform)' == '' "">AnyCPU</Platform>",
                @"    <ProductVersion>{1}</ProductVersion>",
                @"    <SchemaVersion>2.0</SchemaVersion>",
                @"    <RootNamespace>{8}</RootNamespace>",
                @"    <ProjectGuid>{{{2}}}</ProjectGuid>",
                @"    <OutputType>Library</OutputType>",
                @"    <AppDesignerFolder>Properties</AppDesignerFolder>",
                @"    <AssemblyName>{7}</AssemblyName>",
                @"    <TargetFrameworkVersion>{9}</TargetFrameworkVersion>",
                @"    <FileAlignment>512</FileAlignment>",
                @"    <BaseDirectory>{11}</BaseDirectory>",
                @"  </PropertyGroup>",
                @"  <PropertyGroup Condition="" '$(Configuration)|$(Platform)' == 'Debug|AnyCPU' "">",
                @"    <DebugSymbols>true</DebugSymbols>",
                @"    <DebugType>full</DebugType>",
                @"    <Optimize>false</Optimize>",
                @"    <OutputPath>Temp\bin\Debug\</OutputPath>",
                @"    <DefineConstants>{5}</DefineConstants>",
                @"    <ErrorReport>prompt</ErrorReport>",
                @"    <WarningLevel>4</WarningLevel>",
                @"    <NoWarn>0169</NoWarn>",
                @"    <AllowUnsafeBlocks>{12}</AllowUnsafeBlocks>",
                @"  </PropertyGroup>",
                @"  <PropertyGroup Condition="" '$(Configuration)|$(Platform)' == 'Release|AnyCPU' "">",
                @"    <DebugType>pdbonly</DebugType>",
                @"    <Optimize>true</Optimize>",
                @"    <OutputPath>Temp\bin\Release\</OutputPath>",
                @"    <ErrorReport>prompt</ErrorReport>",
                @"    <WarningLevel>4</WarningLevel>",
                @"    <NoWarn>0169</NoWarn>",
                @"    <AllowUnsafeBlocks>{12}</AllowUnsafeBlocks>",
                @"  </PropertyGroup>",
            };

            var forceExplicitReferences = new string[]
            {
                @"  <PropertyGroup>",
                @"    <NoConfig>true</NoConfig>",
                @"    <NoStdLib>true</NoStdLib>",
                @"    <AddAdditionalExplicitAssemblyReferences>false</AddAdditionalExplicitAssemblyReferences>",
                @"    <ImplicitlyExpandNETStandardFacades>false</ImplicitlyExpandNETStandardFacades>",
                @"    <ImplicitlyExpandDesignTimeFacades>false</ImplicitlyExpandDesignTimeFacades>",
                @"  </PropertyGroup>",
            };

            var itemGroupStart = new[]
            {
                @"  <ItemGroup>",
            };

            /*var systemReferences = new string[] {
                @"    <Reference Include=""System"" />",
                @"    <Reference Include=""System.Xml"" />",
                @"    <Reference Include=""System.Core"" />",
                @"    <Reference Include=""System.Runtime.Serialization"" />",
                @"    <Reference Include=""System.Xml.Linq"" />",
            };*/

            var footer = new string[]
            {
                @"    <Reference Include=""UnityEngine"">",
                @"      <HintPath>{3}</HintPath>",
                @"    </Reference>",
                @"    <Reference Include=""UnityEditor"">",
                @"      <HintPath>{4}</HintPath>",
                @"    </Reference>",
                @"  </ItemGroup>",
                @"  <ItemGroup>",
                @""
            };

            var text = header.Concat(forceExplicitReferences).Concat(itemGroupStart).Concat(footer).ToArray();
            return string.Join("\r\n", text);
        }

        void SyncSolution(IEnumerable<Assembly> islands)
        {
            SyncSolutionFileIfNotChanged(SolutionFile(), SolutionText(islands, ModeForCurrentExternalEditor()));
        }

        static Mode ModeForCurrentExternalEditor()
        {
            return Mode.UnityScriptAsPrecompiledAssembly;
        }

        string SolutionText(IEnumerable<Assembly> islands, Mode mode)
        {
            var fileversion = "11.00";
            var vsversion = "2010";

            var relevantIslands = RelevantIslandsForMode(islands, mode);
            string projectEntries = GetProjectEntries(relevantIslands);
            string projectConfigurations = string.Join(k_WindowsNewline, relevantIslands.Select(i => GetProjectActiveConfigurations(ProjectGuid(i.outputPath))).ToArray());
            return string.Format(GetSolutionText(), fileversion, vsversion, projectEntries, projectConfigurations, ReadExistingMonoDevelopSolutionProperties());
        }

        static IEnumerable<Assembly> RelevantIslandsForMode(IEnumerable<Assembly> islands, Mode mode)
        {
            IEnumerable<Assembly> relevantIslands = islands.Where(i => mode == Mode.UnityScriptAsUnityProj || ScriptingLanguage.CSharp == ScriptingLanguageFor(i));
            return relevantIslands;
        }

        /// <summary>
        /// Get a Project("{guid}") = "MyProject", "MyProject.unityproj", "{projectguid}"
        /// entry for each relevant language
        /// </summary>
        internal string GetProjectEntries(IEnumerable<Assembly> islands)
        {
            var projectEntries = islands.Select(i => string.Format(
                solutionProjectEntryTemplate,
                SolutionGuid(i), Path.GetFileNameWithoutExtension(i.outputPath), Path.GetFileName(ProjectFile(i)), ProjectGuid(i.outputPath)
            ));

            return string.Join(k_WindowsNewline, projectEntries.ToArray());
        }

        /// <summary>
        /// Generate the active configuration string for a given project guid
        /// </summary>
        string GetProjectActiveConfigurations(string projectGuid)
        {
            return string.Format(
                solutionProjectConfigurationTemplate,
                projectGuid);
        }

        string EscapedRelativePathFor(string file)
        {
            var projectDir = ProjectDirectory.Replace('/', '\\');
            file = file.Replace('/', '\\');
            var path = SkipPathPrefix(file, projectDir);
//            if (PackageManager.Folders.IsPackagedAssetPath(path.Replace('\\', '/')))
//            {
//                // We have to normalize the path, because the PackageManagerRemapper assumes
//                // dir seperators will be os specific.
//                var absolutePath = Path.GetFullPath(NormalizePath(path)).Replace('/', '\\');
//                path = SkipPathPrefix(absolutePath, projectDir);
//            }

            return SecurityElement.Escape(path);
        }

        static string SkipPathPrefix(string path, string prefix)
        {
            if (path.StartsWith(prefix))
                return path.Substring(prefix.Length + 1);
            return path;
        }

        static string NormalizePath(string path)
        {
            if (Path.DirectorySeparatorChar == '\\')
                return path.Replace('/', Path.DirectorySeparatorChar);
            return path.Replace('\\', Path.DirectorySeparatorChar);
        }


        string ProjectGuid(string assembly)
        {
            return SolutionGuidGenerator.GuidForProject(m_ProjectName + Path.GetFileNameWithoutExtension(assembly));
        }

        string SolutionGuid(Assembly island)
        {
            return SolutionGuidGenerator.GuidForSolution(m_ProjectName, GetExtensionOfSourceFiles(island.sourceFiles));
        }

        string ProjectFooter(Assembly island)
        {
            return string.Format(GetProjectFooterTemplate(), ReadExistingMonoDevelopProjectProperties(island));
        }

        string ReadExistingMonoDevelopSolutionProperties()
        {
            if (!HasSolutionBeenGenerated()) return k_DefaultMonoDevelopSolutionProperties;
            string[] lines;
            try
            {
                lines = File.ReadAllLines(SolutionFile());
            }
            catch (IOException)
            {
                return k_DefaultMonoDevelopSolutionProperties;
            }

            StringBuilder existingOptions = new StringBuilder();
            bool collecting = false;

            foreach (string line in lines)
            {
                if (k_MonoDevelopPropertyHeader.IsMatch(line))
                {
                    collecting = true;
                }

                if (collecting)
                {
                    if (line.Contains("EndGlobalSection"))
                    {
                        existingOptions.Append(line);
                        collecting = false;
                    }
                    else
                        existingOptions.AppendFormat("{0}{1}", line, k_WindowsNewline);
                }
            }

            if (0 < existingOptions.Length)
            {
                return existingOptions.ToString();
            }

            return k_DefaultMonoDevelopSolutionProperties;
        }

        string ReadExistingMonoDevelopProjectProperties(Assembly island)
        {
            if (!ProjectExists(island)) return string.Empty;
            XmlDocument doc = new XmlDocument();
            XmlNamespaceManager manager;
            try
            {
                doc.Load(ProjectFile(island));
                manager = new XmlNamespaceManager(doc.NameTable);
                manager.AddNamespace("msb", MSBuildNamespaceUri);
            }
            catch (Exception ex)
            {
                if (ex is IOException ||
                    ex is XmlException)
                    return string.Empty;
                throw;
            }

            XmlNodeList nodes = doc.SelectNodes("/msb:Project/msb:ProjectExtensions", manager);
            if (0 == nodes.Count) return string.Empty;

            StringBuilder sb = new StringBuilder();
            foreach (XmlNode node in nodes)
            {
                sb.AppendLine(node.OuterXml);
            }

            return sb.ToString();
        }

        static string GetProjectExtension()
        {
            return ".csproj";
        }

        void WriteVSCodeSettingsFiles()
        {
            var vsCodeDirectory = Path.Combine(ProjectDirectory, ".vscode");

            if (!Directory.Exists(vsCodeDirectory))
                Directory.CreateDirectory(vsCodeDirectory);

            var vsCodeSettingsJson = Path.Combine(vsCodeDirectory, "settings.json");

            if (!File.Exists(vsCodeSettingsJson))
                File.WriteAllText(vsCodeSettingsJson, k_SettingsJson);
        }
    }
}
