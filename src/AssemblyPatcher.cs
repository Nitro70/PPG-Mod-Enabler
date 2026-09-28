using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace PPGModEnabler
{
    internal enum BuildState
    {
        SafeMode,     // the Sept 22 2026 build with mods switched off: can be patched
        Patched,      // already patched by this tool
        NotSafeMode,  // a build where the mod loader is not stubbed (mods should already work)
        Unsupported,  // anything else: we do not touch it
        Unreadable,   // the file is missing or not a valid assembly (for example a half-written file)
    }

    internal sealed class Inspection
    {
        public BuildState State;
        public string Detail;
        public bool KnownFile;   // byte-for-byte a game file this tool was tested on
    }

    internal sealed class PatchException : Exception
    {
        public PatchException(string message) : base(message) { }
    }

    /// <summary>
    /// Re-enables the mod loader in Assembly-CSharp.dll. The safe-mode build did not remove the mod
    /// system, it replaced four small pieces with stubs:
    ///   1. ModLoader.Compile            returned "Safe mode is enabled" instead of compiling
    ///   2. ModLoader.LoadModAt          marked every mod as broken with that same message
    ///   3. ModificationManager.Invoke   returned false instead of calling the mod's Main/OnLoad/OnUnload
    ///   4. ModEntryBehaviour.UpdateUi   printed "Safe mode is enabled. Mods cannot load." in the mod list
    /// Everything else (the Roslyn compiler, its suspicious-code scan, assembly loading, ModAPI) is intact.
    /// Every edit stays inside Assembly-CSharp.dll, so restoring that one file undoes the patch.
    /// </summary>
    internal static class AssemblyPatcher
    {
        public const string PatchVersion = "1.0.0";
        public const string MarkerTypeName = "PPGModEnablerPatch";
        private const string SafeModeText = "Safe mode is enabled";
        private const string SafeModeUiText = "Safe mode is enabled. Mods cannot load.";
        private const string LogPrefix = "[PPG Mod Enabler] ";

        // SHA-256 of the unpatched Assembly-CSharp.dll of every build this tool was tested on.
        // Steam delivers identical files to everyone on the same build.
        private static readonly string[] KnownSafeModeFiles =
        {
            "83597029aadc84f24324858afb64997a6b29e0e402fea206eaa38101bd6bc395", // build 25467926, 22 Sept 2026
        };

        public static Inspection Inspect(string dllPath, string managedDir)
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(dllPath);
            }
            catch (Exception ex)
            {
                return new Inspection { State = BuildState.Unreadable, Detail = "the game file could not be read (" + ex.Message + ")" };
            }
            return Inspect(bytes, managedDir);
        }

        public static Inspection Inspect(byte[] bytes, string managedDir)
        {
            Inspection result;
            try
            {
                using (var loaded = new LoadedModule(bytes, managedDir))
                    result = Inspect(loaded.Module);
            }
            catch (Exception ex)
            {
                return new Inspection { State = BuildState.Unreadable, Detail = "the game file is damaged or not a game file (" + ex.Message + ")" };
            }
            result.KnownFile = KnownSafeModeFiles.Contains(SafeCopy.HashHex(bytes));
            return result;
        }

        /// <summary>Patches the DLL bytes in memory and returns the new bytes. Throws PatchException if anything is unexpected.</summary>
        public static byte[] BuildPatched(byte[] original, string managedDir)
        {
            byte[] patched;
            using (var loaded = new LoadedModule(original, managedDir))
            {
                Inspection before = Inspect(loaded.Module);
                if (before.State != BuildState.SafeMode)
                    throw new PatchException("the game file is not the safe-mode build (" + before.Detail + ")");

                var plan = new Plan(loaded.Module);
                plan.Apply();
                AddMarker(loaded.Module);

                using (var stream = new MemoryStream())
                {
                    loaded.Module.Write(stream);
                    patched = stream.ToArray();
                }
            }

            // Read the result back to make sure it is a valid assembly that we recognise as patched.
            using (var check = new LoadedModule(patched, managedDir))
            {
                Inspection after = Inspect(check.Module);
                if (after.State != BuildState.Patched)
                    throw new PatchException("the patched file did not pass the self-check (" + after.Detail + ")");
            }
            return patched;
        }

        private static Inspection Inspect(ModuleDefinition module)
        {
            TypeDefinition marker = module.GetType(MarkerTypeName);
            if (marker != null)
            {
                string version = marker.Fields.FirstOrDefault(f => f.Name == "Version")?.Constant as string;
                return new Inspection { State = BuildState.Patched, Detail = "patched by PPG Mod Enabler " + (version ?? "(unknown version)") };
            }

            TypeDefinition loader = module.GetType("ModLoader");
            TypeDefinition manager = module.GetType("ModificationManager");
            MethodDefinition compile = loader?.Methods.FirstOrDefault(m => m.Name == "Compile" && m.Parameters.Count == 1);
            MethodDefinition loadModAt = loader?.Methods.FirstOrDefault(m => m.Name == "LoadModAt" && m.Parameters.Count == 1);
            MethodDefinition invoke = manager?.Methods.FirstOrDefault(m => m.Name == "Invoke" && m.Parameters.Count == 2);
            if (compile == null || loadModAt == null || invoke == null || !compile.HasBody || !loadModAt.HasBody || !invoke.HasBody)
                return new Inspection { State = BuildState.Unsupported, Detail = "the C# mod loader is not in this game version" };

            bool compileStub = HasString(compile, SafeModeText);
            bool loadStub = HasString(loadModAt, SafeModeText);
            bool invokeStub = invoke.Body.Instructions.Count == 2
                && invoke.Body.Instructions[0].OpCode == OpCodes.Ldc_I4_0
                && invoke.Body.Instructions[1].OpCode == OpCodes.Ret;

            if (!compileStub && !loadStub && !invokeStub)
                return new Inspection { State = BuildState.NotSafeMode, Detail = "mods are not switched off in this game version" };
            if (!(compileStub && loadStub && invokeStub))
                return new Inspection { State = BuildState.Unsupported, Detail = "this game version is only partly in safe mode, which this tool does not know" };

            try
            {
                new Plan(module); // finds every piece the patch needs, without changing anything
            }
            catch (PatchException ex)
            {
                return new Inspection { State = BuildState.Unsupported, Detail = "safe-mode build, but different code than expected: " + ex.Message };
            }
            return new Inspection { State = BuildState.SafeMode, Detail = "safe-mode build, mods are switched off" };
        }

        private static bool HasString(MethodDefinition method, string text)
        {
            return method.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == text);
        }

        private static void AddMarker(ModuleDefinition module)
        {
            var type = new TypeDefinition("", MarkerTypeName,
                TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit | TypeAttributes.Class,
                module.TypeSystem.Object);
            type.Fields.Add(new FieldDefinition("Version", FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault, module.TypeSystem.String)
            {
                Constant = PatchVersion,
            });
            module.Types.Add(type);
        }

        /// <summary>Owns a module and the resolver it uses, so file handles on the game folder are released.</summary>
        private sealed class LoadedModule : IDisposable
        {
            private readonly DefaultAssemblyResolver resolver;
            public readonly ModuleDefinition Module;

            public LoadedModule(byte[] bytes, string managedDir)
            {
                resolver = new DefaultAssemblyResolver();
                resolver.AddSearchDirectory(managedDir);
                Module = ModuleDefinition.ReadModule(new MemoryStream(bytes), new ReaderParameters
                {
                    AssemblyResolver = resolver,
                    ReadingMode = ReadingMode.Immediate,
                    InMemory = true,
                });
            }

            public void Dispose()
            {
                Module.Dispose();
                resolver.Dispose();
            }
        }

        /// <summary>Finds every method, field and instruction the patch touches. Throws if any is missing.</summary>
        private sealed class Plan
        {
            private readonly ModuleDefinition m;

            private readonly TypeDefinition modScript;
            private readonly TypeDefinition entryBehaviour;
            private readonly MethodDefinition compile;
            private readonly MethodDefinition tryCompile;
            private readonly MethodDefinition loadModAt;
            private readonly MethodDefinition invoke;
            private readonly MethodDefinition updateUi;
            private readonly MethodDefinition resultCtor;
            private readonly MethodDefinition setSuspicious;
            private readonly MethodDefinition resultSuspiciousGetter; // null when Suspicious is a field
            private readonly MethodReference tupleCtor;
            private readonly MethodReference tryGetValue;
            private readonly MethodReference logError;
            private readonly MethodReference logWarning;
            private readonly FieldDefinition modScripts;
            private readonly FieldDefinition apiMetaDataIsValid;
            private readonly MethodDefinition apiSetMetadata;
            private readonly FieldDefinition loadedAssembly;
            private readonly FieldDefinition metaActive, metaEntryPoint, metaName, metaHasErrors, metaErrors, metaDescription;
            private readonly FieldDefinition entryModMeta, entryDescription;
            private readonly FieldDefinition resultSuccess;
            private readonly FieldDefinition resultSuspiciousField;   // null when Suspicious is a property
            private readonly Instruction loadHasErrorsTrue, loadErrorsText;
            private readonly Instruction[] uiColor, uiFontSize;
            private readonly Instruction uiText;

            public Plan(ModuleDefinition module)
            {
                m = module;
                TypeDefinition loader = FindType("ModLoader");
                TypeDefinition manager = FindType("ModificationManager");
                TypeDefinition meta = FindType("ModMetaData");
                TypeDefinition result = FindType("ModCompilationResult");
                modScript = FindType("ModScript");
                entryBehaviour = FindType("ModEntryBehaviour");

                compile = Method(loader, "Compile", 1);
                tryCompile = Method(loader, "TryCompileMod", 2);
                loadModAt = Method(loader, "LoadModAt", 1);
                invoke = Method(manager, "Invoke", 2);
                updateUi = Method(entryBehaviour, "UpdateUi", 0);

                Expect(Same(compile.Parameters[0].ParameterType, meta), "ModLoader.Compile takes a ModMetaData");
                Expect(Same(tryCompile.Parameters[0].ParameterType, meta)
                    && tryCompile.Parameters[1].ParameterType is ByReferenceType byRef && Same(byRef.ElementType, modScript)
                    && Same(tryCompile.ReturnType, result), "ModLoader.TryCompileMod(ModMetaData, out ModScript) returns ModCompilationResult");
                Expect(Same(invoke.Parameters[0].ParameterType, m.TypeSystem.String) && Same(invoke.Parameters[1].ParameterType, meta)
                    && Same(invoke.ReturnType, m.TypeSystem.Boolean), "ModificationManager.Invoke(string, ModMetaData) returns bool");

                TypeDefinition modApi = FindType("ModAPI");
                apiMetaDataIsValid = Field(modApi, "metaDataIsValid");
                Expect(apiMetaDataIsValid.IsStatic && Same(apiMetaDataIsValid.FieldType, m.TypeSystem.Boolean), "ModAPI.metaDataIsValid is a static bool");
                apiSetMetadata = modApi.Properties.FirstOrDefault(p => p.Name == "Metadata")?.SetMethod;
                Expect(apiSetMetadata != null && apiSetMetadata.IsStatic, "ModAPI.Metadata has a static setter");
                modScripts = Field(loader, "ModScripts");
                Expect(modScripts.IsStatic, "ModLoader.ModScripts is static");
                loadedAssembly = Field(modScript, "LoadedAssembly");
                metaActive = Field(meta, "Active");
                metaEntryPoint = Field(meta, "EntryPoint");
                metaName = Field(meta, "Name");
                metaHasErrors = Field(meta, "HasErrors");
                metaErrors = Field(meta, "Errors");
                metaDescription = Field(meta, "Description");
                entryModMeta = Field(entryBehaviour, "ModMeta");
                entryDescription = Field(entryBehaviour, "Description");
                resultSuccess = Field(result, "Success");

                setSuspicious = meta.Properties.FirstOrDefault(p => p.Name == "Suspicious")?.SetMethod;
                Expect(setSuspicious != null, "ModMetaData.Suspicious has a setter");
                resultSuspiciousField = result.Fields.FirstOrDefault(f => f.Name == "Suspicious");
                if (resultSuspiciousField == null)
                    resultSuspiciousGetter = result.Properties.FirstOrDefault(p => p.Name == "Suspicious")?.GetMethod;
                Expect(resultSuspiciousField != null || resultSuspiciousGetter != null, "ModCompilationResult.Suspicious exists");

                resultCtor = result.Methods.FirstOrDefault(c => c.IsConstructor && !c.IsStatic && c.Parameters.Count == 3
                    && Same(c.Parameters[0].ParameterType, m.TypeSystem.Boolean)
                    && Same(c.Parameters[1].ParameterType, m.TypeSystem.String)
                    && Same(c.Parameters[2].ParameterType, m.TypeSystem.Boolean));
                Expect(resultCtor != null, "ModCompilationResult(bool, string, bool) exists");

                tupleCtor = compile.Body.Instructions
                    .Where(i => i.OpCode == OpCodes.Newobj)
                    .Select(i => i.Operand as MethodReference)
                    .SingleOrDefault(r => r != null && r.DeclaringType.Name == "ValueTuple`2");
                Expect(tupleCtor != null, "ModLoader.Compile builds a (result, script) tuple");

                tryGetValue = loader.Methods.Where(x => x.HasBody).SelectMany(x => x.Body.Instructions)
                    .Select(i => i.Operand as MethodReference)
                    .FirstOrDefault(r => r != null && r.Name == "TryGetValue"
                        && r.DeclaringType is GenericInstanceType g && g.ElementType.FullName == "System.Collections.Generic.Dictionary`2"
                        && g.GenericArguments.Count == 2 && Same(g.GenericArguments[0], meta) && Same(g.GenericArguments[1], modScript));
                Expect(tryGetValue != null, "ModLoader uses ModScripts.TryGetValue");

                logError = ExistingCall("System.Void UnityEngine.Debug::LogError(System.Object)");
                logWarning = ExistingCall("System.Void UnityEngine.Debug::LogWarning(System.Object)");

                // The emitted code relies on these shapes; refuse anything else instead of guessing.
                Expect(compile.IsStatic && tryCompile.IsStatic && invoke.IsStatic && !updateUi.IsStatic,
                    "Compile, TryCompileMod and Invoke are static and UpdateUi is not");
                Expect(result.IsValueType && !meta.IsValueType && !modScript.IsValueType,
                    "ModCompilationResult is a struct and ModMetaData/ModScript are classes");
                Expect(Same(compile.ReturnType, tupleCtor.DeclaringType), "Compile returns the (result, script) tuple");
                Expect(Same(modScripts.FieldType, tryGetValue.DeclaringType), "ModLoader.ModScripts is a Dictionary<ModMetaData, ModScript>");
                Expect(loadedAssembly.FieldType.FullName == "System.Reflection.Assembly", "ModScript.LoadedAssembly is an Assembly");
                Expect(new[] { metaActive, metaHasErrors, resultSuccess }.All(f => Same(f.FieldType, m.TypeSystem.Boolean)),
                    "Active, HasErrors and Success are bools");
                Expect(new[] { metaEntryPoint, metaName, metaErrors, metaDescription }.All(f => Same(f.FieldType, m.TypeSystem.String)),
                    "EntryPoint, Name, Errors and Description are strings");
                Expect(Same(entryModMeta.FieldType, meta), "ModEntryBehaviour.ModMeta is a ModMetaData");
                Expect(!apiMetaDataIsValid.DeclaringType.HasGenericParameters && apiSetMetadata.Parameters.Count == 1
                    && Same(apiSetMetadata.Parameters[0].ParameterType, meta), "ModAPI.Metadata takes a ModMetaData");
                Expect(resultSuspiciousField == null || Same(resultSuspiciousField.FieldType, m.TypeSystem.Boolean), "Suspicious is a bool");

                // The patch promises that the game's own suspicious-code check stays on. That check is switched on
                // inside TryCompileMod; if a future build moved it, patching would silently drop it.
                Expect(tryCompile.Body.Instructions.Any(i => i.Operand is MemberReference r
                        && (r.Name == "RejectShadyCode" || r.Name == "set_RejectShadyCode" || r.Name == "get_RejectShadyCode")),
                    "TryCompileMod still switches on the game's suspicious-code check (RejectShadyCode)");

                // LoadModAt: modMetaData.HasErrors = true; modMetaData.Errors = "Safe mode is enabled";
                loadHasErrorsTrue = Single(loadModAt, (i, n) => i.OpCode == OpCodes.Ldc_I4_1 && IsStore(n, metaHasErrors),
                    "LoadModAt sets HasErrors = true");
                loadErrorsText = Single(loadModAt, (i, n) => i.OpCode == OpCodes.Ldstr && (string)i.Operand == SafeModeText && IsStore(n, metaErrors),
                    "LoadModAt sets Errors = \"Safe mode is enabled\"");

                // UpdateUi: Description.color = Color.cyan; Description.fontSize += 2f; Description.text = "Safe mode ...";
                uiColor = Sequence(updateUi, "the cyan description colour",
                    i => i.OpCode == OpCodes.Ldarg_0,
                    i => IsLoad(i, entryDescription),
                    i => IsCallTo(i, "UnityEngine.Color", "get_cyan"),
                    i => IsCallTo(i, "UnityEngine.UI.Graphic", "set_color"));
                uiFontSize = Sequence(updateUi, "the growing description font size",
                    i => i.OpCode == OpCodes.Ldarg_0,
                    i => IsLoad(i, entryDescription),
                    i => i.OpCode == OpCodes.Dup,
                    i => IsCallTo(i, "TMPro.TMP_Text", "get_fontSize"),
                    i => i.OpCode == OpCodes.Ldc_R4 && (float)i.Operand == 2f,
                    i => i.OpCode == OpCodes.Add,
                    i => IsCallTo(i, "TMPro.TMP_Text", "set_fontSize"));
                uiText = Single(updateUi, (i, n) => i.OpCode == OpCodes.Ldstr && (string)i.Operand == SafeModeUiText && IsCallTo(n, "TMPro.TMP_Text", "set_text"),
                    "the \"Safe mode is enabled\" text in the mod list");
            }

            public void Apply()
            {
                PatchCompile();
                PatchLoadModAt();
                PatchInvoke();
                PatchUpdateUi();
            }

            // (result, script) Compile(ModMetaData mod)
            // {
            //     ModScript script; ModCompilationResult result;
            //     try { result = TryCompileMod(mod, out script); }
            //     catch (Exception ex) { result = new ModCompilationResult(false, prefix + ex, false); script = null; }
            //     mod.HasErrors = !result.Success;
            //     mod.Suspicious = result.Suspicious;
            //     if (result.Success) mod.Errors = null;
            //     return (result, script);
            // }
            private void PatchCompile()
            {
                MethodBody body = Reset(compile);
                var script = new VariableDefinition(modScript);
                var result = new VariableDefinition(resultCtor.DeclaringType);
                var ex = new VariableDefinition(Import(typeof(Exception)));
                body.Variables.Add(script);
                body.Variables.Add(result);
                body.Variables.Add(ex);
                ILProcessor il = body.GetILProcessor();

                Instruction tryStart = il.Create(OpCodes.Ldarg_0);
                Instruction handler = il.Create(OpCodes.Stloc, ex);
                Instruction after = il.Create(OpCodes.Ldarg_0);
                Instruction keepErrors = il.Create(OpCodes.Ldloc, result);

                il.Append(tryStart);
                il.Emit(OpCodes.Ldloca, script);
                il.Emit(OpCodes.Call, tryCompile);
                il.Emit(OpCodes.Stloc, result);
                il.Emit(OpCodes.Leave, after);

                il.Append(handler);
                il.Emit(OpCodes.Ldloca, result);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ldstr, LogPrefix + "compiling failed: ");
                il.Emit(OpCodes.Ldloc, ex);
                il.Emit(OpCodes.Callvirt, Import(typeof(object).GetMethod("ToString", Type.EmptyTypes)));
                il.Emit(OpCodes.Call, Concat(2));
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Call, resultCtor);
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Stloc, script);
                il.Emit(OpCodes.Leave, after);

                il.Append(after);                       // mod
                il.Emit(OpCodes.Ldloca, result);
                il.Emit(OpCodes.Ldfld, resultSuccess);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ceq);
                il.Emit(OpCodes.Stfld, metaHasErrors);

                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldloca, result);
                if (resultSuspiciousField != null)
                    il.Emit(OpCodes.Ldfld, resultSuspiciousField);
                else
                    il.Emit(OpCodes.Call, resultSuspiciousGetter);
                il.Emit(OpCodes.Callvirt, setSuspicious);

                il.Emit(OpCodes.Ldloca, result);
                il.Emit(OpCodes.Ldfld, resultSuccess);
                il.Emit(OpCodes.Brfalse, keepErrors);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Stfld, metaErrors);

                il.Append(keepErrors);                  // result
                il.Emit(OpCodes.Ldloc, script);
                il.Emit(OpCodes.Newobj, tupleCtor);
                il.Emit(OpCodes.Ret);

                body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
                {
                    TryStart = tryStart,
                    TryEnd = handler,
                    HandlerStart = handler,
                    HandlerEnd = after,
                    CatchType = Import(typeof(Exception)),
                });
            }

            // LoadModAt: HasErrors = false and Errors = null instead of flagging every mod as "Safe mode is enabled".
            private void PatchLoadModAt()
            {
                loadHasErrorsTrue.OpCode = OpCodes.Ldc_I4_0;
                loadErrorsText.OpCode = OpCodes.Ldnull;
                loadErrorsText.Operand = null;
            }

            // bool Invoke(string methodName, ModMetaData metaData)
            // {
            //     if (metaData == null || !metaData.Active) return false;
            //     if (!ModLoader.ModScripts.TryGetValue(metaData, out var script) || script?.LoadedAssembly == null) return false;
            //     bool ran = false;
            //     try
            //     {
            //         Type type = script.LoadedAssembly.GetType(metaData.EntryPoint);
            //         if (type == null) { Debug.LogWarning(...); }
            //         else
            //         {
            //             MethodInfo method = type.GetMethod(methodName, Public | NonPublic | Static);
            //             if (method != null)
            //             {
            //                 ModAPI.Metadata = metaData;          // same context the game sets for AfterSpawn,
            //                 ModAPI.metaDataIsValid = true;       // so ModAPI.Register etc. work in Main/OnLoad
            //                 method.Invoke(null, null);
            //                 ran = true;
            //             }
            //         }
            //     }
            //     catch (Exception ex) { Debug.LogError(prefix + metaData.Name + " failed in " + methodName + ": " + ex); }
            //     ModAPI.metaDataIsValid = false;
            //     return ran;
            // }
            private void PatchInvoke()
            {
                MethodBody body = Reset(invoke);
                var script = new VariableDefinition(modScript);
                var type = new VariableDefinition(Import(typeof(Type)));
                var method = new VariableDefinition(Import(typeof(System.Reflection.MethodInfo)));
                var ran = new VariableDefinition(m.TypeSystem.Boolean);
                var ex = new VariableDefinition(Import(typeof(Exception)));
                body.Variables.Add(script);
                body.Variables.Add(type);
                body.Variables.Add(method);
                body.Variables.Add(ran);
                body.Variables.Add(ex);
                ILProcessor il = body.GetILProcessor();

                Instruction returnFalse = il.Create(OpCodes.Ldc_I4_0);
                Instruction end = il.Create(OpCodes.Ldc_I4_0);
                Instruction tryStart = il.Create(OpCodes.Ldloc, script);
                Instruction noType = il.Create(OpCodes.Ldstr, LogPrefix + "could not find the entry class '");
                Instruction leaveTry = il.Create(OpCodes.Leave, end);
                Instruction handler = il.Create(OpCodes.Stloc, ex);

                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Brfalse, returnFalse);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldfld, metaActive);
                il.Emit(OpCodes.Brfalse, returnFalse);
                il.Emit(OpCodes.Ldsfld, modScripts);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldloca, script);
                il.Emit(OpCodes.Callvirt, tryGetValue);
                il.Emit(OpCodes.Brfalse, returnFalse);
                il.Emit(OpCodes.Ldloc, script);
                il.Emit(OpCodes.Brfalse, returnFalse);
                il.Emit(OpCodes.Ldloc, script);
                il.Emit(OpCodes.Ldfld, loadedAssembly);
                il.Emit(OpCodes.Brfalse, returnFalse);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Stloc, ran);

                il.Append(tryStart);                    // script
                il.Emit(OpCodes.Ldfld, loadedAssembly);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldfld, metaEntryPoint);
                il.Emit(OpCodes.Callvirt, Import(typeof(System.Reflection.Assembly).GetMethod("GetType", new[] { typeof(string) })));
                il.Emit(OpCodes.Stloc, type);
                il.Emit(OpCodes.Ldloc, type);
                il.Emit(OpCodes.Brfalse, noType);
                il.Emit(OpCodes.Ldloc, type);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldc_I4, (int)(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static));
                il.Emit(OpCodes.Callvirt, Import(typeof(Type).GetMethod("GetMethod", new[] { typeof(string), typeof(System.Reflection.BindingFlags) })));
                il.Emit(OpCodes.Stloc, method);
                il.Emit(OpCodes.Ldloc, method);
                il.Emit(OpCodes.Brfalse, leaveTry);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Call, apiSetMetadata);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Stsfld, apiMetaDataIsValid);
                il.Emit(OpCodes.Ldloc, method);
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Callvirt, Import(typeof(System.Reflection.MethodBase).GetMethod("Invoke", new[] { typeof(object), typeof(object[]) })));
                il.Emit(OpCodes.Pop);
                il.Emit(OpCodes.Ldc_I4_1);
                il.Emit(OpCodes.Stloc, ran);
                il.Emit(OpCodes.Leave, end);

                il.Append(noType);                      // "...entry class '"
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldfld, metaEntryPoint);
                il.Emit(OpCodes.Ldstr, "' in mod ");
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldfld, metaName);
                il.Emit(OpCodes.Call, Concat(4));
                il.Emit(OpCodes.Call, logWarning);
                il.Append(leaveTry);

                il.Append(handler);
                il.Emit(OpCodes.Ldstr, LogPrefix);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldfld, metaName);
                il.Emit(OpCodes.Ldstr, " failed in ");
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Call, Concat(4));
                il.Emit(OpCodes.Ldstr, ": ");
                il.Emit(OpCodes.Ldloc, ex);
                il.Emit(OpCodes.Callvirt, Import(typeof(object).GetMethod("ToString", Type.EmptyTypes)));
                il.Emit(OpCodes.Call, Concat(3));
                il.Emit(OpCodes.Call, logError);
                il.Emit(OpCodes.Leave, end);

                il.Append(end);                         // ModAPI.metaDataIsValid = false
                il.Emit(OpCodes.Stsfld, apiMetaDataIsValid);
                il.Emit(OpCodes.Ldloc, ran);
                il.Emit(OpCodes.Ret);
                il.Append(returnFalse);
                il.Emit(OpCodes.Ret);

                body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
                {
                    TryStart = tryStart,
                    TryEnd = handler,
                    HandlerStart = handler,
                    HandlerEnd = end,
                    CatchType = Import(typeof(Exception)),
                });
            }

            // Mod list: drop the cyan colour and the font size that grew on every refresh, and show the
            // mod's description (or its error) instead of "Safe mode is enabled. Mods cannot load."
            private void PatchUpdateUi()
            {
                foreach (Instruction i in uiColor.Concat(uiFontSize))
                {
                    i.OpCode = OpCodes.Nop;
                    i.Operand = null;
                }

                MethodDefinition describe = AddDescribeMethod();
                ILProcessor il = updateUi.Body.GetILProcessor();
                uiText.OpCode = OpCodes.Ldarg_0;
                uiText.Operand = null;
                Instruction loadMeta = il.Create(OpCodes.Ldfld, entryModMeta);
                il.InsertAfter(uiText, loadMeta);
                il.InsertAfter(loadMeta, il.Create(OpCodes.Call, describe));
            }

            // private static string PPGModEnabler_Describe(ModMetaData meta)
            // {
            //     if (meta == null) return "";
            //     if (!meta.HasErrors) return meta.Description ?? "";
            //     string s = meta.Errors ?? "This mod failed to load. The game log (Player.log) has details.";
            //     if (s.Length > 300) s = s.Substring(0, 300) + "...";
            //     return s;
            // }
            // Plain text only: this text field has rich text switched off, so tags would show literally.
            private MethodDefinition AddDescribeMethod()
            {
                var describe = new MethodDefinition("PPGModEnabler_Describe",
                    MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, m.TypeSystem.String);
                describe.Parameters.Add(new ParameterDefinition("meta", ParameterAttributes.None, metaHasErrors.DeclaringType));
                var s = new VariableDefinition(m.TypeSystem.String);
                describe.Body.Variables.Add(s);
                describe.Body.InitLocals = true;
                ILProcessor il = describe.Body.GetILProcessor();

                Instruction empty = il.Create(OpCodes.Ldstr, "");
                Instruction error = il.Create(OpCodes.Ldarg_0);
                Instruction haveText = il.Create(OpCodes.Ldloc, s);
                Instruction done = il.Create(OpCodes.Ldloc, s);

                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Brfalse, empty);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, metaHasErrors);
                il.Emit(OpCodes.Brtrue, error);
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldfld, metaDescription);
                il.Emit(OpCodes.Dup);
                Instruction returnDescription = il.Create(OpCodes.Ret);
                il.Emit(OpCodes.Brtrue, returnDescription);
                il.Emit(OpCodes.Pop);
                il.Emit(OpCodes.Ldstr, "");
                il.Append(returnDescription);

                il.Append(error);                       // meta
                il.Emit(OpCodes.Ldfld, metaErrors);
                il.Emit(OpCodes.Stloc, s);
                il.Emit(OpCodes.Ldloc, s);
                il.Emit(OpCodes.Brtrue, haveText);
                il.Emit(OpCodes.Ldstr, "This mod failed to load. The game log (Player.log) has details.");
                il.Emit(OpCodes.Stloc, s);

                il.Append(haveText);                    // s
                il.Emit(OpCodes.Callvirt, Import(typeof(string).GetProperty("Length").GetGetMethod()));
                il.Emit(OpCodes.Ldc_I4, 300);
                il.Emit(OpCodes.Ble, done);
                il.Emit(OpCodes.Ldloc, s);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Ldc_I4, 300);
                il.Emit(OpCodes.Callvirt, Import(typeof(string).GetMethod("Substring", new[] { typeof(int), typeof(int) })));
                il.Emit(OpCodes.Ldstr, "...");
                il.Emit(OpCodes.Call, Concat(2));
                il.Emit(OpCodes.Stloc, s);

                il.Append(done);                        // s
                il.Emit(OpCodes.Ret);

                il.Append(empty);
                il.Emit(OpCodes.Ret);

                entryBehaviour.Methods.Add(describe);
                return describe;
            }

            // ---- lookups ----

            private TypeDefinition FindType(string name)
            {
                TypeDefinition t = m.GetType(name);
                Expect(t != null, "type " + name + " exists");
                return t;
            }

            private static MethodDefinition Method(TypeDefinition type, string name, int parameters)
            {
                MethodDefinition[] found = type.Methods.Where(x => x.Name == name && x.Parameters.Count == parameters && x.HasBody).ToArray();
                Expect(found.Length == 1, type.Name + "." + name + " exists exactly once");
                return found[0];
            }

            private static FieldDefinition Field(TypeDefinition type, string name)
            {
                FieldDefinition f = type.Fields.FirstOrDefault(x => x.Name == name);
                Expect(f != null, type.Name + "." + name + " exists");
                return f;
            }

            private MethodReference ExistingCall(string fullName)
            {
                MethodReference found = m.GetTypes().SelectMany(t => t.Methods).Where(x => x.HasBody)
                    .SelectMany(x => x.Body.Instructions)
                    .Select(i => i.Operand as MethodReference)
                    .FirstOrDefault(r => r != null && r.FullName == fullName);
                Expect(found != null, "the game calls " + fullName);
                return found;
            }

            private static Instruction Single(MethodDefinition method, Func<Instruction, Instruction, bool> match, string what)
            {
                Instruction[] hits = method.Body.Instructions.Where(i => i.Next != null && match(i, i.Next)).ToArray();
                Expect(hits.Length == 1, what + " (found " + hits.Length + " matches, expected 1)");
                return hits[0];
            }

            private static Instruction[] Sequence(MethodDefinition method, string what, params Func<Instruction, bool>[] steps)
            {
                var list = method.Body.Instructions;
                Instruction[] result = null;
                int count = 0;
                for (int start = 0; start + steps.Length <= list.Count; start++)
                {
                    bool ok = true;
                    for (int k = 0; k < steps.Length && ok; k++)
                        ok = steps[k](list[start + k]);
                    if (ok)
                    {
                        count++;
                        result = list.Skip(start).Take(steps.Length).ToArray();
                    }
                }
                Expect(count == 1, what + " in the mod list (found " + count + " matches, expected 1)");
                return result;
            }

            private static bool Same(TypeReference a, TypeReference b) =>
                a != null && b != null && a.FullName == b.FullName;

            private static bool IsStore(Instruction i, FieldDefinition field) =>
                i.OpCode == OpCodes.Stfld && i.Operand is FieldReference f && f.Resolve() == field;

            private static bool IsLoad(Instruction i, FieldDefinition field) =>
                i.OpCode == OpCodes.Ldfld && i.Operand is FieldReference f && f.Resolve() == field;

            private static bool IsCallTo(Instruction i, string declaringType, string name) =>
                (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) && i.Operand is MethodReference r
                && r.Name == name && r.DeclaringType.FullName == declaringType;

            private static MethodBody Reset(MethodDefinition method)
            {
                MethodBody body = method.Body;
                body.Instructions.Clear();
                body.Variables.Clear();
                body.ExceptionHandlers.Clear();
                body.InitLocals = true;
                return body;
            }

            private TypeReference Import(Type type) => m.ImportReference(type);

            private MethodReference Import(System.Reflection.MethodBase method)
            {
                Expect(method != null, "a .NET method needed by the patch exists");
                return m.ImportReference(method);
            }

            private MethodReference Concat(int parts) =>
                Import(typeof(string).GetMethod("Concat", Enumerable.Repeat(typeof(string), parts).ToArray()));

            private static void Expect(bool condition, string what)
            {
                if (!condition)
                    throw new PatchException("expected: " + what);
            }
        }
    }
}
