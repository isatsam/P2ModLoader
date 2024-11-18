using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Mono.Cecil;
using Mono.Cecil.Cil;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;
using Microsoft.CodeAnalysis.Emit;
using P2ModLoader.AssemblyPatching;
using P2ModLoader.Helper;
using LanguageVersion = Microsoft.CodeAnalysis.CSharp.LanguageVersion;
using MethodBody = Mono.Cecil.Cil.MethodBody;

public class AssemblyPatcher {
    public static bool PatchAssembly(string dllPath, string updatedSourcePath, string outputPath = null) {
        outputPath ??= dllPath;
        try {
            string dllDirectory = Path.GetDirectoryName(Path.GetFullPath(dllPath));
            string fileCopy = Path.Combine(Path.GetDirectoryName(dllPath),
                $"{Path.GetFileNameWithoutExtension(dllPath)}Temp.dll");
            File.Copy(dllPath, fileCopy, true);

            var references = ReferenceCollector.CollectReferences(dllDirectory, dllPath, fileCopy);

            string updatedSource = File.ReadAllText(updatedSourcePath);
            var updatedTree = CSharpSyntaxTree.ParseText(
                updatedSource,
                new CSharpParseOptions(LanguageVersion.Latest)
            );

            var updatedRoot = updatedTree.GetRoot();
            var methodDeclarations = updatedRoot.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .ToList();

            if (!methodDeclarations.Any()) {
                ErrorHandler.Handle($"Could not find any methods in the source file {updatedSourcePath}", null);
                return false;
            }

            var methodsByType = methodDeclarations.GroupBy(md => {
                var classDecl = md.Ancestors().OfType<ClassDeclarationSyntax>().First();
                var namespaceDecl = classDecl.Ancestors().OfType<NamespaceDeclarationSyntax>().FirstOrDefault();
                string namespaceName = namespaceDecl?.Name.ToString() ?? "";
                string typeName = classDecl.Identifier.Text;
                string fullTypeName = string.IsNullOrEmpty(namespaceName) ? typeName : $"{namespaceName}.{typeName}";
                return new { FullTypeName = fullTypeName, NamespaceDecl = namespaceDecl, ClassDecl = classDecl };
            }).ToList();

            var decompiler = new CSharpDecompiler(dllPath, new DecompilerSettings() {
                ShowXmlDocumentation = false,
                RemoveDeadCode = false,
                RemoveDeadStores = false
            });

            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(dllDirectory);

            var readerParams = new ReaderParameters {
                AssemblyResolver = resolver,
                ReadWrite = true
            };

            var backupPath = dllPath + ".backup";
            if (File.Exists(backupPath)) File.Delete(backupPath);
            File.Copy(dllPath, backupPath);

            using var originalAssembly = AssemblyDefinition.ReadAssembly(backupPath, readerParams);

            foreach (var typeGroup in methodsByType) {
                string fullTypeName = typeGroup.Key.FullTypeName;
                var namespaceDecl = typeGroup.Key.NamespaceDecl;
                var classDecl = typeGroup.Key.ClassDecl;
                Logger.LogInfo($"Processing type: {fullTypeName}");

                var originalType = originalAssembly.MainModule.GetType(fullTypeName);

                if (originalType == null) {
                    Logger.LogInfo($"Type {fullTypeName} not found in original assembly. Adding new type.");

                    var compilationUnit = SyntaxFactory.CompilationUnit();

                    var usings = ReferenceCollector.CollectAllUsings(updatedRoot);
                    compilationUnit = compilationUnit.WithUsings(usings);

                    if (namespaceDecl != null) {
                        var namespaceSyntax = SyntaxFactory.NamespaceDeclaration(namespaceDecl.Name)
                            .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(classDecl));
                        compilationUnit =
                            compilationUnit.WithMembers(
                                SyntaxFactory.SingletonList<MemberDeclarationSyntax>(namespaceSyntax));
                    } else {
                        compilationUnit =
                            compilationUnit.WithMembers(
                                SyntaxFactory.SingletonList<MemberDeclarationSyntax>(classDecl));
                    }

                    var syntaxTree = compilationUnit.SyntaxTree;

                    var compilation = CSharpCompilation.Create(
                        Path.GetRandomFileName(),
                        new[] { syntaxTree },
                        references,
                        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                            .WithOptimizationLevel(OptimizationLevel.Release)
                            .WithPlatform(Platform.AnyCpu)
                    );

                    using var ms = new MemoryStream();
                    var result = compilation.Emit(ms);

                    if (!result.Success) {
                        PrintCompilationFailure(result, syntaxTree);
                        return false;
                    }

                    ms.Seek(0, SeekOrigin.Begin);

                    using var newAssembly =
                        AssemblyDefinition.ReadAssembly(new MemoryStream(ms.ToArray()), readerParams);

                    var newType = newAssembly.MainModule.GetType(fullTypeName);
                    if (newType == null) {
                        ErrorHandler.Handle($"Could not find type {fullTypeName} in the compiled assembly", null);
                        return false;
                    }

                    var importedType = CloneCreator.CloneType(newType, originalAssembly.MainModule);
                    originalAssembly.MainModule.Types.Add(importedType);

                    Logger.LogInfo($"Added new type {fullTypeName} to original assembly.");
                } else {
                    string decompiledSource;
                    try {
                        decompiledSource = decompiler.DecompileTypeAsString(new FullTypeName(fullTypeName));
                    } catch (Exception ex) {
                        ErrorHandler.Handle($"Failed to decompile type {fullTypeName}: {ex.Message}", null);
                        return false;
                    }

                    var decompTree = CSharpSyntaxTree.ParseText(decompiledSource);

                    var decompRoot = decompTree.GetRoot() as CompilationUnitSyntax;

                    if (decompRoot == null) {
                        ErrorHandler.Handle("Failed to parse decompiled source.", null);
                        return false;
                    }

                    var mergedUsings = ReferenceCollector.MergeUsings(decompRoot, updatedRoot);

                    var decompClass = FindClassDeclaration(decompRoot, classDecl.Identifier.Text);

                    if (decompClass == null) {
                        ErrorHandler.Handle($"Failed to find class {classDecl.Identifier.Text} in source.", null);
                        return false;
                    }

                    var rewriter = new MethodReplaceRewriter(typeGroup.ToDictionary(
                        m => m.Identifier.Text,
                        m => m
                    ));

                    var modifiedClass = rewriter.Visit(decompClass) as ClassDeclarationSyntax;
                    if (modifiedClass == null) {
                        ErrorHandler.Handle("Failed to modify class with new methods.", null);
                        return false;
                    }

                    CompilationUnitSyntax mergedRoot;
                    if (namespaceDecl != null) {
                        var mergedNamespace = SyntaxFactory.NamespaceDeclaration(namespaceDecl.Name)
                            .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(modifiedClass));
                        mergedRoot = SyntaxFactory.CompilationUnit()
                            .WithUsings(mergedUsings)
                            .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(mergedNamespace));
                    } else {
                        mergedRoot = SyntaxFactory.CompilationUnit()
                            .WithUsings(mergedUsings)
                            .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(modifiedClass));
                    }

                    var mergedSource = mergedRoot.NormalizeWhitespace().ToFullString();
                    var mergedTree = CSharpSyntaxTree.ParseText(mergedSource);

                    var compilation = CSharpCompilation.Create(
                        Path.GetRandomFileName(),
                        new[] { mergedTree },
                        references,
                        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                            .WithOptimizationLevel(OptimizationLevel.Release)
                            .WithPlatform(Platform.AnyCpu)
                    );

                    using var ms = new MemoryStream();
                    var result = compilation.Emit(ms);

                    if (!result.Success) {
                        PrintCompilationFailure(result, mergedTree);
                        return false;
                    }

                    ms.Seek(0, SeekOrigin.Begin);

                    using var newAssembly =
                        AssemblyDefinition.ReadAssembly(new MemoryStream(ms.ToArray()), readerParams);

                    var newType = newAssembly.MainModule.GetType(fullTypeName);

                    if (newType == null) {
                        ErrorHandler.Handle($"Could not find type {fullTypeName} in the compiled assembly", null);
                        return false;
                    }

                    foreach (var methodName in typeGroup.Select(m => m.Identifier.Text)) {
                        var newMethod = newType.Methods.FirstOrDefault(m => m.Name == methodName);
                        var originalMethod = originalType.Methods.FirstOrDefault(m => m.Name == methodName);

                        if (newMethod == null) {
                            Logger.LogWarning($"Could not find method {methodName} in the compiled assembly");
                            continue;
                        }

                        if (originalMethod == null) {
                            Logger.LogInfo($"Adding new method {methodName} to type {fullTypeName}");
                            var importedMethod = CloneCreator.CloneMethod(newMethod, originalAssembly.MainModule);
                            originalType.Methods.Add(importedMethod);
                            Logger.LogInfo($"Added new method {methodName}");
                        } else {
                            ReplaceMethodBody(originalMethod, newMethod, originalAssembly.MainModule);
                            Logger.LogInfo($"Replaced method {methodName} in type {fullTypeName}");
                        }
                    }
                }
            }

            originalAssembly.Write(outputPath);
            Logger.LogInfo($"Successfully patched assembly at {outputPath}");
            return true;
        } catch (Exception ex) {
            ErrorHandler.Handle("Error patching assembly", ex);
            return false;
        }
    }

    private static ClassDeclarationSyntax FindClassDeclaration(SyntaxNode node, string className)
    {
        foreach (var child in node.ChildNodes())
        {
            if (child is ClassDeclarationSyntax cls && cls.Identifier.Text == className)
                return cls;

            var result = FindClassDeclaration(child, className);
            if (result != null)
                return result;
        }
        return null;
    }

    private static void PrintCompilationFailure(EmitResult result, SyntaxTree tree) {
        Logger.LogError("Compilation failed!");
        foreach (var diagnostic in result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)) {
            var lineSpan = diagnostic.Location.GetLineSpan();
            var sourceText = tree.GetText();
            var errorLine = lineSpan.StartLinePosition.Line < sourceText.Lines.Count
                ? sourceText.Lines[lineSpan.StartLinePosition.Line].ToString()
                : "<unknown>";

            Logger.LogInfo($"{diagnostic.Id}: {diagnostic.GetMessage()}");
            Logger.LogInfo($"Location: {lineSpan}");
            Logger.LogInfo($"Source line: {errorLine}");
            Logger.LogLineBreak();
        }
    }

    private class MethodReplaceRewriter(Dictionary<string, MethodDeclarationSyntax> methods) : CSharpSyntaxRewriter {
        public override SyntaxNode VisitMethodDeclaration(MethodDeclarationSyntax node) {
            if (methods.TryGetValue(node.Identifier.Text, out var replacementMethod)) {
                return replacementMethod
                    .WithModifiers(node.Modifiers)
                    .WithAttributeLists(node.AttributeLists);
            }
            return node;
        }
    }

    private static void ReplaceMethodBody(MethodDefinition originalMethod, MethodDefinition newMethod,
        ModuleDefinition targetModule) {
        originalMethod.Body = new MethodBody(originalMethod);

        var variableMap = new Dictionary<VariableDefinition, VariableDefinition>();
        foreach (var variable in newMethod.Body.Variables) {
            var newVariable = new VariableDefinition(targetModule.ImportReference(variable.VariableType));
            originalMethod.Body.Variables.Add(newVariable);
            variableMap[variable] = newVariable;
        }

        var parameterMap = new Dictionary<ParameterDefinition, ParameterDefinition>();
        for (int i = 0; i < newMethod.Parameters.Count; i++) {
            parameterMap[newMethod.Parameters[i]] = originalMethod.Parameters[i];
        }

        originalMethod.Body.InitLocals = newMethod.Body.InitLocals;
        originalMethod.Body.MaxStackSize = newMethod.Body.MaxStackSize;

        var ilProcessor = originalMethod.Body.GetILProcessor();
        var instructionMap = new Dictionary<Instruction, Instruction>();

        var currentType = originalMethod.DeclaringType;
        foreach (var instruction in newMethod.Body.Instructions) {
            var newInstruction = CloneCreator.CloneInstruction(instruction, targetModule, variableMap, parameterMap, instructionMap, currentType);
            instructionMap[instruction] = newInstruction;
            ilProcessor.Append(newInstruction);
        }

        foreach (var instruction in originalMethod.Body.Instructions) {
            if (instruction.Operand is Instruction targetInstruction && instructionMap.ContainsKey(targetInstruction)) {
                instruction.Operand = instructionMap[targetInstruction];
            } else if (instruction.Operand is Instruction[] targetInstructions) {
                instruction.Operand = targetInstructions
                    .Select(ti => instructionMap.ContainsKey(ti) ? instructionMap[ti] : ti).ToArray();
            }
        }

        foreach (var handler in newMethod.Body.ExceptionHandlers) {
            var newHandler = new ExceptionHandler(handler.HandlerType) {
                CatchType = handler.CatchType != null ? targetModule.ImportReference(handler.CatchType) : null,
                TryStart = instructionMap[handler.TryStart],
                TryEnd = instructionMap[handler.TryEnd],
                HandlerStart = instructionMap[handler.HandlerStart],
                HandlerEnd = instructionMap[handler.HandlerEnd]
            };
            originalMethod.Body.ExceptionHandlers.Add(newHandler);
        }
    }
}