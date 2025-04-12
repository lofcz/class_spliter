using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace Splitter;

internal static class Program
{
    private const int DefaultMaxLines = 1_500;

    private static void Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine($"Usage: ClassSplitter.exe <filepath> [maxlines={DefaultMaxLines}]");
            return;
        }

        string filePath = args[0];
        int maxLinesPerFile = DefaultMaxLines; // Default value

        // Parse maxlines argument if provided
        var maxLinesArg = args.FirstOrDefault(a => a.StartsWith("maxlines=", StringComparison.OrdinalIgnoreCase));
        if (maxLinesArg != null)
        {
            if (int.TryParse(maxLinesArg.Split('=')[1], out int parsedMaxLines))
            {
                maxLinesPerFile = parsedMaxLines;
            }
            else
            {
                Console.WriteLine($"Invalid maxlines value: {maxLinesArg.Split('=')[1]}. Using default: {DefaultMaxLines}");
            }
        }
        else if (args.Length > 1) // Support positional argument for backward compatibility (less robust)
        {
             if (int.TryParse(args[1], out int parsedMaxLines))
             {
                 maxLinesPerFile = parsedMaxLines;
             }
             else
             {
                 Console.WriteLine($"Invalid maxlines value: {args[1]}. Using default: {DefaultMaxLines}");
             }
        }


        if (!File.Exists(filePath))
        {
            Console.WriteLine($"File {filePath} not found.");
            return;
        }

        Console.WriteLine($"Processing {filePath} with max lines per file: {maxLinesPerFile}");
        SplitClass(filePath, maxLinesPerFile);
    }

    private static void SplitClass(string filePath, int maxLinesPerFile)
    {
        string sourceCode = File.ReadAllText(filePath);
        string fileName = Path.GetFileNameWithoutExtension(filePath);
        string extension = Path.GetExtension(filePath);
        string directory = Path.GetDirectoryName(filePath) ?? ".";

        SyntaxTree tree = CSharpSyntaxTree.ParseText(sourceCode, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest));
        CompilationUnitSyntax root = tree.GetCompilationUnitRoot();
        var diagnostics = tree.GetDiagnostics();
        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            Console.WriteLine("Error parsing file. Please fix syntax errors:");
            foreach (var diag in diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
            {
                Console.WriteLine($"- {diag}");
            }
            return;
        }


        SyntaxList<UsingDirectiveSyntax> usings = root.Usings;

        // Find the class declaration, handling different namespace types
        ClassDeclarationSyntax? originalClassDeclaration = null;
        BaseNamespaceDeclarationSyntax? namespaceSyntax = null; // Covers both NamespaceDeclarationSyntax and FileScopedNamespaceDeclarationSyntax
        string? namespaceName = null;
        bool isFileScopedNamespace = false;

        // Try finding class within a namespace (block or file-scoped)
        namespaceSyntax = root.Members.OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        if (namespaceSyntax != null)
        {
            originalClassDeclaration = namespaceSyntax.Members.OfType<ClassDeclarationSyntax>().FirstOrDefault();
            namespaceName = namespaceSyntax.Name.ToString();
            isFileScopedNamespace = namespaceSyntax is FileScopedNamespaceDeclarationSyntax;
        }

        // If not found in a namespace, try finding it directly in the root
        originalClassDeclaration ??= root.Members.OfType<ClassDeclarationSyntax>().FirstOrDefault();

        if (originalClassDeclaration == null)
        {
            Console.WriteLine("No class declaration found in the file.");
            return;
        }

        // Check initial line count - if already compliant, no need to split
        int initialLineCount = CountLines(sourceCode);
        if (initialLineCount <= maxLinesPerFile)
        {
            Console.WriteLine($"Class '{originalClassDeclaration.Identifier.Text}' doesn't need to be split ({initialLineCount} lines <= {maxLinesPerFile}).");
            return;
        }

        Console.WriteLine($"Initial class '{originalClassDeclaration.Identifier.Text}' has {initialLineCount} lines, exceeding limit of {maxLinesPerFile}. Splitting...");

        List<MemberDeclarationSyntax> allMembers = originalClassDeclaration.Members.ToList();
        List<List<MemberDeclarationSyntax>> newFilesMembers = []; // Members for the new partial files
        List<MemberDeclarationSyntax> membersToKeepInOriginal = []; // Members to keep in the first (original) file

        // --- Distribution Logic ---
        List<MemberDeclarationSyntax> currentFileMembers = [];
        bool firstFile = true;

        // Use a temporary workspace for formatting to get accurate line counts
        using var workspace = new AdhocWorkspace();

        foreach (MemberDeclarationSyntax member in allMembers)
        {
            List<MemberDeclarationSyntax> testMembers = [.. currentFileMembers, member];
            string testContent;
            int currentFileLines;

            if (firstFile)
            {
                // Test adding to the original file
                testContent = GenerateFileContent(workspace, usings, namespaceSyntax, originalClassDeclaration, testMembers, true); // isOriginal=true
                currentFileLines = CountLines(testContent);

                if (currentFileLines <= maxLinesPerFile)
                {
                    currentFileMembers.Add(member); // Add to current (original) file's list
                }
                else
                {
                    // Current member makes the original file too large.
                    // Finalize the original file with members collected so far.
                    membersToKeepInOriginal.AddRange(currentFileMembers);
                    firstFile = false; // Start filling new files

                    // Now check if the current member *alone* fits in a *new* file
                    currentFileMembers = [member]; // Start new file list with current member
                    testContent = GenerateFileContent(workspace, usings, namespaceSyntax, originalClassDeclaration, currentFileMembers, false); // isOriginal=false
                    currentFileLines = CountLines(testContent);

                    if (currentFileLines > maxLinesPerFile)
                    {
                        // Even a single member is too large for a new file!
                        Console.WriteLine($"Warning: Member starting on line {member.GetLocation().GetLineSpan().StartLinePosition.Line + 1} is too large ({currentFileLines} lines with overhead) to fit within the {maxLinesPerFile} line limit even in its own file. It will be placed in a new file exceeding the limit.");
                        // Add this oversized member list to newFilesMembers and clear currentFileMembers
                        newFilesMembers.Add(currentFileMembers);
                        currentFileMembers = [];
                    }
                    // If it fits alone, currentFileMembers is correctly initialized for the next iteration
                }
            }
            else // Filling subsequent new files
            {
                testContent = GenerateFileContent(workspace, usings, namespaceSyntax, originalClassDeclaration, testMembers, false); // isOriginal=false
                currentFileLines = CountLines(testContent);

                if (currentFileLines <= maxLinesPerFile)
                {
                    currentFileMembers.Add(member); // Add to current new file's list
                }
                else
                {
                    // Current member makes the *current new file* too large.
                    // Finalize the previous new file.
                    if (currentFileMembers.Count > 0) // Ensure we don't add empty lists
                    {
                        newFilesMembers.Add(currentFileMembers);
                    }

                    // Start a new file list with the current member
                    currentFileMembers = [member];

                    // Re-check if this single member fits in a new file (edge case)
                    testContent = GenerateFileContent(workspace, usings, namespaceSyntax, originalClassDeclaration, currentFileMembers, false); // isOriginal=false
                    currentFileLines = CountLines(testContent);
                    if (currentFileLines > maxLinesPerFile)
                    {
                        Console.WriteLine($"Warning: Member starting on line {member.GetLocation().GetLineSpan().StartLinePosition.Line + 1} is too large ({currentFileLines} lines with overhead) to fit within the {maxLinesPerFile} line limit even in its own file. It will be placed in a new file exceeding the limit.");
                        // Add this oversized member list to newFilesMembers and clear currentFileMembers
                        newFilesMembers.Add(currentFileMembers);
                        currentFileMembers = [];
                    }
                     // If it fits alone, currentFileMembers is correctly initialized for the next iteration
                }
            }
        }

        // Add the last collected members
        if (firstFile)
        {
            // All members fit in the original file (should have been caught earlier, but safety check)
            membersToKeepInOriginal.AddRange(currentFileMembers);
        }
        else if (currentFileMembers.Count > 0)
        {
            // Add the last batch of members for a new file
            newFilesMembers.Add(currentFileMembers);
        }


        // --- File Generation ---

        if (newFilesMembers.Count == 0)
        {
            // This should ideally not happen if the initial check passed and splitting was needed,
            // but handle it defensively.
             int finalOriginalLineCount = CountLines(GenerateFileContent(workspace, usings, namespaceSyntax, originalClassDeclaration, membersToKeepInOriginal, true));
            Console.WriteLine($"Splitting resulted in no new files. Original file has {membersToKeepInOriginal.Count} members and {finalOriginalLineCount} lines.");
             if (finalOriginalLineCount > maxLinesPerFile) {
                 Console.WriteLine($"Warning: The original file still exceeds the line limit ({finalOriginalLineCount} > {maxLinesPerFile}). This might indicate an issue with a single large member or the overhead calculation.");
             }
             // Optionally, still write the potentially modified original file if membersToKeepInOriginal differs from allMembers
             // ModifyOriginalClassToPartial(workspace, filePath, root, usings, namespaceSyntax, originalClassDeclaration, membersToKeepInOriginal);
            return; // Exit if no new files were generated
        }

        // Modify the original file
        ModifyOriginalClassToPartial(workspace, filePath, root, usings, namespaceSyntax, originalClassDeclaration, membersToKeepInOriginal);

        // Create new partial class files
        int startNumber = FindNextAvailableNumber(directory, fileName, extension);
        for (int i = 0; i < newFilesMembers.Count; i++)
        {
            int fileNumber = startNumber + i;
            string partialFileName = $"{fileName}{fileNumber}{extension}";
            string partialFilePath = Path.Combine(directory, partialFileName);
            List<MemberDeclarationSyntax> membersForThisFile = newFilesMembers[i];

            string fileContent = GenerateFileContent(workspace, usings, namespaceSyntax, originalClassDeclaration, membersForThisFile, false); // isOriginal=false
            int lineCount = CountLines(fileContent);

            // Ensure the generated content doesn't accidentally exceed the limit (sanity check)
             if (lineCount > maxLinesPerFile)
             {
                 Console.WriteLine($"Warning: Generated file '{partialFilePath}' has {lineCount} lines, exceeding the limit of {maxLinesPerFile}. This likely means a single member plus overhead was too large.");
             }

            File.WriteAllText(partialFilePath, fileContent);
            Console.WriteLine($"Created file: {partialFileName} with {membersForThisFile.Count} members and {lineCount} lines.");
        }

        Console.WriteLine($"Class split into {newFilesMembers.Count + 1} files (original + {newFilesMembers.Count} new), numbered starting from {startNumber}.");
    }

    // --- Helper Methods ---

    /// <summary>
    /// Generates the complete C# file content for a partial class.
    /// </summary>
     private static string GenerateFileContent(
        Workspace workspace,
        SyntaxList<UsingDirectiveSyntax> usings,
        BaseNamespaceDeclarationSyntax? namespaceSyntax,
        ClassDeclarationSyntax originalClassDeclaration,
        List<MemberDeclarationSyntax> members,
        bool isOriginalFileModification)
    {
        // 1. Create the partial class declaration (handles 'partial' keyword, members, braces)
        ClassDeclarationSyntax partialClass = CreatePartialClassDeclaration(originalClassDeclaration, members, isOriginalFileModification);

        // 2. Prepare the top-level member (namespace or class)
        MemberDeclarationSyntax topLevelMember;
        if (namespaceSyntax is FileScopedNamespaceDeclarationSyntax fileScopedNamespace)
        {
            // Ensure exactly ONE newline after the semicolon. Formatter handles the rest.
            var semicolonWithTrivia = fileScopedNamespace.SemicolonToken
                .WithTrailingTrivia(SyntaxFactory.ElasticEndOfLine(Environment.NewLine)); // SINGLE newline

            topLevelMember = fileScopedNamespace
                .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(partialClass))
                .WithSemicolonToken(semicolonWithTrivia);
        }
        else if (namespaceSyntax is NamespaceDeclarationSyntax blockNamespace)
        {
            // Ensure ONE newline after the opening brace.
            var openBraceWithTrivia = blockNamespace.OpenBraceToken
                .WithTrailingTrivia(SyntaxFactory.ElasticEndOfLine(Environment.NewLine)); // SINGLE newline

            // Ensure ONE newline before the closing brace.
            var closeBraceWithTrivia = blockNamespace.CloseBraceToken
                 .WithLeadingTrivia(SyntaxFactory.ElasticEndOfLine(Environment.NewLine)); // SINGLE newline

            topLevelMember = blockNamespace
                .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(partialClass))
                .WithOpenBraceToken(openBraceWithTrivia)
                .WithCloseBraceToken(closeBraceWithTrivia);
        }
        else // No namespace
        {
            topLevelMember = partialClass;
        }

        // 3. Create the compilation unit and handle usings trivia
        CompilationUnitSyntax compilationUnit = SyntaxFactory.CompilationUnit();

        if (usings.Count > 0)
        {
            // Ensure the last using has appropriate trailing trivia
            // We want ONE blank line (two newlines) between usings and the next element (namespace or class)
            var lastUsing = usings.Last();
            var triviaList = new List<SyntaxTrivia> { SyntaxFactory.ElasticEndOfLine(Environment.NewLine) };

            // Check if the next element is not immediately following (needs a blank line)
            // This check is simplified: assume a blank line is always desired after usings if something follows.
            if (namespaceSyntax != null || members.Count > 0 || topLevelMember is ClassDeclarationSyntax) // Check if there's something after usings
            {
                 triviaList.Add(SyntaxFactory.ElasticEndOfLine(Environment.NewLine)); // Add the second newline for a blank line
            }

            var lastUsingWithTrivia = lastUsing.WithTrailingTrivia(triviaList);
            compilationUnit = compilationUnit.WithUsings(usings.Replace(lastUsing, lastUsingWithTrivia));
        }


        // Add the main content (namespace or class)
        compilationUnit = compilationUnit.AddMembers(topLevelMember);


        // 4. Format the code (crucial for accurate line count and final appearance)
        // Consider customizing formatting options if default behavior isn't perfect
        // var options = workspace.Options; //.WithChangedOption(...);
        SyntaxNode formattedNode = Formatter.Format(compilationUnit, workspace/*, options*/);
        return formattedNode.ToFullString();
    }
    
    /// <summary>
    /// Creates a ClassDeclarationSyntax, ensuring it has the partial modifier
    /// and includes the specified members. Handles trivia for braces.
    /// </summary>
    private static ClassDeclarationSyntax CreatePartialClassDeclaration(
        ClassDeclarationSyntax originalClassDeclaration,
        List<MemberDeclarationSyntax> members,
        bool isOriginalFileModification)
    {
        SyntaxTokenList modifiers = originalClassDeclaration.Modifiers;
        bool needsPartialKeyword = !modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword));

        if (needsPartialKeyword)
        {
            SyntaxToken partialKeyword = SyntaxFactory.Token(
                    SyntaxFactory.TriviaList(), // No leading trivia here
                    SyntaxKind.PartialKeyword,
                    SyntaxFactory.TriviaList(SyntaxFactory.Space)); // Space after partial

            int insertIndex = 0; // Default: beginning
             var accessModifiers = new[] { SyntaxKind.PublicKeyword, SyntaxKind.InternalKeyword, SyntaxKind.ProtectedKeyword, SyntaxKind.PrivateKeyword };
             var otherModifiers = new[] { SyntaxKind.StaticKeyword, SyntaxKind.AbstractKeyword, SyntaxKind.SealedKeyword }; // Order matters slightly

             var lastAccess = modifiers.LastOrDefault(m => accessModifiers.Contains(m.Kind()));
             var lastOther = modifiers.LastOrDefault(m => otherModifiers.Contains(m.Kind()));

             if (lastAccess.IsKind(SyntaxKind.None) && lastOther.IsKind(SyntaxKind.None)) {
                 insertIndex = 0; // No modifiers, add at start
             } else if (!lastAccess.IsKind(SyntaxKind.None) && lastOther.IsKind(SyntaxKind.None)) {
                 insertIndex = modifiers.IndexOf(lastAccess) + 1; // After access modifier
             } else if (lastAccess.IsKind(SyntaxKind.None) && !lastOther.IsKind(SyntaxKind.None)) {
                 insertIndex = modifiers.IndexOf(lastOther) + 1; // After other modifier (e.g. static partial class)
             } else { // Both exist, put after the one that appears later
                 insertIndex = Math.Max(modifiers.IndexOf(lastAccess), modifiers.IndexOf(lastOther)) + 1;
             }

            modifiers = modifiers.Insert(insertIndex, partialKeyword);
        }
        // If it's already partial, we just use the existing modifiers list.

        // Adjust trivia for class braces for better formatting control by Formatter
        SyntaxToken openBrace = originalClassDeclaration.OpenBraceToken
            .WithLeadingTrivia(SyntaxFactory.TriviaList(SyntaxFactory.Space)) // Space before {
            .WithTrailingTrivia(SyntaxFactory.TriviaList(SyntaxFactory.ElasticEndOfLine(Environment.NewLine))); // Newline after {

        SyntaxToken closeBrace = originalClassDeclaration.CloseBraceToken
             .WithLeadingTrivia(SyntaxFactory.TriviaList(SyntaxFactory.ElasticEndOfLine(Environment.NewLine))); // Newline before }
             // Trailing trivia for close brace is handled by container or EOF

        // Create the class node. Crucially, don't add explicit leading trivia here.
        // Let the trivia from the preceding element (namespace ;, namespace {, or using) dictate the spacing.
        return originalClassDeclaration
            .WithModifiers(modifiers)
            .WithMembers(SyntaxFactory.List(members))
            .WithOpenBraceToken(openBrace)
            .WithCloseBraceToken(closeBrace);
            // If the very first token of the modified class needs specific *leading* trivia adjustment,
            // it might be better done *after* this creation, just before adding to the namespace/compilation unit,
            // but relying on the formatter is usually preferred.
    }


    /// <summary>
    /// Modifies the original class file to contain only the specified members
    /// and ensures the 'partial' modifier is present.
    /// </summary>
    private static void ModifyOriginalClassToPartial(
        Workspace workspace,
        string filePath,
        CompilationUnitSyntax root,
        SyntaxList<UsingDirectiveSyntax> usings,
        BaseNamespaceDeclarationSyntax? namespaceSyntax,
        ClassDeclarationSyntax originalClassDeclaration,
        List<MemberDeclarationSyntax> membersToKeep)
    {
        string finalContent = GenerateFileContent(workspace, usings, namespaceSyntax, originalClassDeclaration, membersToKeep, true); // isOriginal=true
        int lineCount = CountLines(finalContent);

        File.WriteAllText(filePath, finalContent);
        Console.WriteLine($"Modified original file: {Path.GetFileName(filePath)} with {membersToKeep.Count} members and {lineCount} lines.");
    }

    /// <summary>
    /// Counts lines in a string, handling different newline conventions.
    /// </summary>
    private static int CountLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        // Normalize line endings to \n then split
        string normalizedText = text.Replace("\r\n", "\n").Replace("\r", "\n");
        // Split and count. Add 1 because splitting by N delimiters results in N+1 parts.
        // If the text ends with a newline, the last part will be empty, which is correct.
        return normalizedText.Split('\n').Length;
    }


    /// <summary>
    /// Finds the next available number for partial class files (e.g., MyClass2.cs, MyClass3.cs).
    /// </summary>
    private static int FindNextAvailableNumber(string directory, string fileName, string extension)
    {
        // Regex to match files like "FileName<number>.ext"
        // It captures the number part. Ensures it matches the whole filename.
        Regex regex = new Regex($@"^{Regex.Escape(fileName)}(\d+){Regex.Escape(extension)}$", RegexOptions.IgnoreCase);
        int highestNumber = 1; // Start checking from 2 (so default is 2 if no numbered files exist)

        try
        {
            var existingFiles = Directory.EnumerateFiles(directory, $"{fileName}*{extension}");

            foreach (string file in existingFiles)
            {
                string nameOnly = Path.GetFileName(file);
                Match match = regex.Match(nameOnly);
                if (match.Success)
                {
                    if (int.TryParse(match.Groups[1].Value, out int number))
                    {
                        if (number > highestNumber)
                        {
                            highestNumber = number;
                        }
                    }
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            // Ignore if directory doesn't exist, though it should based on input filePath
            Console.WriteLine($"Warning: Directory not found: {directory}");
            return 2; // Default starting number
        }


        return highestNumber + 1; // Return the next available number
    }
}