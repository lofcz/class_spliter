using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace Splitter;

internal static class Program
{
    private const int DefaultMaxLines = 1_500;
    private static int _maxLinesPerFile = DefaultMaxLines; // Store globally for access in ModifyOriginalClassToPartial warning

    // Helper struct to store member and its estimated LOC
    private readonly record struct MemberInfo(int EstimatedLoc, MemberDeclarationSyntax Member) : IComparable<MemberInfo>
    {
        // Sort primarily by LOC, then by original position as a tie-breaker for stability
        public int CompareTo(MemberInfo other)
        {
            int locComparison = EstimatedLoc.CompareTo(other.EstimatedLoc);
            if (locComparison != 0) return locComparison;
            // Use SpanStart as a proxy for original order
            return Member.SpanStart.CompareTo(other.Member.SpanStart);
        }
    }

    // No longer static - calculated per file
    // private static int _baseOverheadLoc = -1;

    private static async Task<int> Main(string[] args)
    {
        // --- Argument Parsing Setup (System.CommandLine) ---
        Option<FileInfo?> fileOption = new Option<FileInfo?>(
            name: "--file",
            description: "A single C# file to split.")
        { ArgumentHelpName = "path" };
        fileOption.AddAlias("-f");

        Option<FileInfo[]?> filesOption = new Option<FileInfo[]?>(
            name: "--files",
            description: "A list of specific C# files to split.")
        { ArgumentHelpName = "path1 path2...", Arity = ArgumentArity.ZeroOrMore }; // Allows multiple values
        filesOption.AddAlias("-l"); // list

        Option<DirectoryInfo?> directoryOption = new Option<DirectoryInfo?>(
            name: "--directory",
            description: "A directory containing C# files to split.")
        { ArgumentHelpName = "dir_path" };
        directoryOption.AddAlias("-d");

        Option<bool> recursiveOption = new Option<bool>(
            name: "--recursive",
            description: "Process files in subdirectories when using --directory.",
            getDefaultValue: () => false);
        recursiveOption.AddAlias("-r");

        Option<int> maxLinesOption = new Option<int>(
            name: "--max-lines",
            description: "Maximum number of lines allowed per generated file.",
            getDefaultValue: () => DefaultMaxLines)
        { ArgumentHelpName = "count" };
        maxLinesOption.AddAlias("-m");
        maxLinesOption.AddValidator(result =>
        {
            if (result.GetValueOrDefault<int>() <= 0)
            {
                result.ErrorMessage = "Max lines must be a positive number.";
            }
        });

        RootCommand rootCommand = new RootCommand("Splits large C# class files into partial classes based on line count.")
        {
            fileOption,
            filesOption,
            directoryOption,
            recursiveOption,
            maxLinesOption
        };

        rootCommand.SetHandler(async (InvocationContext context) =>
        {
            FileInfo? file = context.ParseResult.GetValueForOption(fileOption);
            FileInfo[]? files = context.ParseResult.GetValueForOption(filesOption);
            DirectoryInfo? directory = context.ParseResult.GetValueForOption(directoryOption);
            bool recursive = context.ParseResult.GetValueForOption(recursiveOption);
            int maxLines = context.ParseResult.GetValueForOption(maxLinesOption);

            await RunSplittingProcess(file, files, directory, recursive, maxLines);
        });

        // --- Input Validation ---
        rootCommand.AddValidator(result =>
        {
            if (result.GetValueForOption(fileOption) == null &&
                (result.GetValueForOption(filesOption) == null || !result.GetValueForOption(filesOption)!.Any()) &&
                result.GetValueForOption(directoryOption) == null)
            {
                result.ErrorMessage = "You must specify input using --file, --files, or --directory.";
            }
            if (result.GetValueForOption(recursiveOption) && result.GetValueForOption(directoryOption) == null)
            {
                result.ErrorMessage = "--recursive can only be used with --directory.";
            }
        });


        return await rootCommand.InvokeAsync(args);
    }

    private static async Task RunSplittingProcess(
        FileInfo? singleFile,
        FileInfo[]? multipleFiles,
        DirectoryInfo? directory,
        bool recursive,
        int maxLines)
    {
        _maxLinesPerFile = maxLines; // Set the global static variable

        HashSet<string> filesToProcess = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // Use HashSet for automatic deduplication

        // 1. Add single file
        if (singleFile != null)
        {
            if (singleFile.Exists)
            {
                filesToProcess.Add(singleFile.FullName);
            }
            else
            {
                Console.WriteLine($"Warning: Specified file not found: {singleFile.FullName}");
            }
        }

        // 2. Add multiple files
        if (multipleFiles != null)
        {
            foreach (FileInfo fileInfo in multipleFiles)
            {
                if (fileInfo.Exists)
                {
                    filesToProcess.Add(fileInfo.FullName);
                }
                else
                {
                    Console.WriteLine($"Warning: Specified file not found: {fileInfo.FullName}");
                }
            }
        }

        // 3. Add files from directory
        if (directory != null)
        {
            if (directory.Exists)
            {
                SearchOption searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                try
                {
                    foreach (string filePath in Directory.EnumerateFiles(directory.FullName, "*.cs", searchOption))
                    {
                        filesToProcess.Add(filePath);
                    }
                }
                catch (Exception ex) when (ex is DirectoryNotFoundException or UnauthorizedAccessException)
                {
                     Console.WriteLine($"Error enumerating directory '{directory.FullName}': {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine($"Warning: Specified directory not found: {directory.FullName}");
            }
        }

        if (!filesToProcess.Any())
        {
            Console.WriteLine("No valid C# files found to process.");
            return;
        }

        Console.WriteLine($"Found {filesToProcess.Count} C# file(s) to process with max lines per file: {_maxLinesPerFile}");
        Console.WriteLine("---");

        int processedCount = 0;
        int splitCount = 0;
        int errorCount = 0;

        foreach (string filePath in filesToProcess.OrderBy(f => f)) // Process in a consistent order
        {
            Console.WriteLine($"Processing: {filePath}");
            try
            {
                bool wasSplit = await SplitClassAsync(filePath, _maxLinesPerFile);
                if (wasSplit)
                {
                    splitCount++;
                }
                processedCount++;
                Console.WriteLine($"Finished: {Path.GetFileName(filePath)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing file '{filePath}': {ex.Message}");
                // Consider logging the stack trace for debugging: Console.WriteLine(ex.StackTrace);
                errorCount++;
            }
            Console.WriteLine("---");
        }

        Console.WriteLine($"Processing complete.");
        Console.WriteLine($"  Total files processed: {processedCount}");
        Console.WriteLine($"  Files split: {splitCount}");
        Console.WriteLine($"  Errors encountered: {errorCount}");
    }


    /// <summary>
    /// Processes a single C# file for potential splitting.
    /// </summary>
    /// <param name="filePath">Path to the C# file.</param>
    /// <param name="maxLinesPerFile">Maximum lines allowed per file.</param>
    /// <returns>True if the file was split or modified, false otherwise.</returns>
    private static async Task<bool> SplitClassAsync(string filePath, int maxLinesPerFile) // Keep parameter for clarity
    {
        string sourceCode;
        try
        {
            sourceCode = await File.ReadAllTextAsync(filePath);
        }
        catch (IOException ex)
        {
            Console.WriteLine($"Error reading file: {ex.Message}");
            return false;
        }

        string fileName = Path.GetFileNameWithoutExtension(filePath);
        string extension = Path.GetExtension(filePath);
        string directory = Path.GetDirectoryName(filePath) ?? ".";

        // --- Parsing and Validation ---
        SyntaxTree tree = CSharpSyntaxTree.ParseText(sourceCode, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest));
        CompilationUnitSyntax root = tree.GetCompilationUnitRoot();
        IEnumerable<Diagnostic> diagnostics = tree.GetDiagnostics();
        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            Console.WriteLine($"Error parsing file '{Path.GetFileName(filePath)}'. Please fix syntax errors:");
            foreach (Diagnostic diag in diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)) Console.WriteLine($"- {diag}");
            return false;
        }

        SyntaxList<UsingDirectiveSyntax> usings = root.Usings;
        ClassDeclarationSyntax? originalClassDeclaration = null;
        BaseNamespaceDeclarationSyntax? namespaceSyntax = null;

        namespaceSyntax = root.Members.OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        if (namespaceSyntax != null)
        {
            originalClassDeclaration = namespaceSyntax.Members.OfType<ClassDeclarationSyntax>().FirstOrDefault();
        }
        originalClassDeclaration ??= root.Members.OfType<ClassDeclarationSyntax>().FirstOrDefault();

        if (originalClassDeclaration == null)
        {
            Console.WriteLine($"No class declaration found in file '{Path.GetFileName(filePath)}'. Skipping.");
            return false;
        }

        string className = originalClassDeclaration.Identifier.Text;
        int initialLineCount = CountLines(sourceCode);
        if (initialLineCount <= maxLinesPerFile)
        {
            Console.WriteLine($"Class '{className}' in '{Path.GetFileName(filePath)}' doesn't need splitting ({initialLineCount} lines <= {maxLinesPerFile}).");
            return false;
        }
        Console.WriteLine($"Initial class '{className}' in '{Path.GetFileName(filePath)}' has {initialLineCount} lines, exceeding limit of {maxLinesPerFile}. Splitting...");


        // --- Pre-calculate Member LOC & Setup Tracking ---
        using AdhocWorkspace workspace = new AdhocWorkspace();
        List<MemberInfo> allMemberInfos = [];
        // Calculate base overhead LOC *for this specific file*
        int baseOverheadLoc = CalculateOverheadLoc(workspace, usings, namespaceSyntax, originalClassDeclaration);

        Console.WriteLine($"  Calculating estimated LOC for {originalClassDeclaration.Members.Count} members (base overhead: {baseOverheadLoc} lines)...");
        foreach (MemberDeclarationSyntax memberSyntax in originalClassDeclaration.Members)
        {
            string memberName = GetMemberName(memberSyntax);
            // Estimate LOC by generating a file with only this member + overhead
            string memberContent = GenerateFileContent(workspace, usings, namespaceSyntax, originalClassDeclaration, [memberSyntax], false, baseOverheadLoc);
            int memberTotalLoc = CountLines(memberContent);
            // Ensure estimate is at least base overhead + 1 line for the member itself.
            int estimatedLoc = Math.Max(baseOverheadLoc + 1, memberTotalLoc);
            allMemberInfos.Add(new MemberInfo(estimatedLoc, memberSyntax));
            // Console.WriteLine($"    - Member '{memberName}' estimated LOC: {estimatedLoc}"); // Optional detailed logging

            if (memberTotalLoc > maxLinesPerFile)
            {
                 Console.WriteLine($"  Warning: Member '{memberName}' (starting near line {memberSyntax.GetLocation().GetLineSpan().StartLinePosition.Line + 1}) is estimated to be too large ({memberTotalLoc} lines with overhead) to fit within the {maxLinesPerFile} line limit even in its own file.");
            }
        }
        Console.WriteLine($"  Finished calculating LOC for {allMemberInfos.Count} members.");

        List<MemberDeclarationSyntax> allMembersInOrder = originalClassDeclaration.Members.ToList();
        List<List<MemberDeclarationSyntax>> newFilesMembers = [];
        List<MemberDeclarationSyntax> membersToKeepInOriginal = [];
        List<MemberDeclarationSyntax> currentFileMembers = [];

        // Track remaining members efficiently
        Dictionary<MemberDeclarationSyntax, MemberInfo> remainingMemberInfos = allMemberInfos.ToDictionary(info => info.Member, info => info);
        // Keep a separate list sorted by LOC for finding fillers
        List<MemberInfo> remainingMembersSortedByLoc = allMemberInfos.OrderBy(m => m).ToList();

        int currentFileLoc = baseOverheadLoc; // Start with overhead for this file
        bool processingOriginalFile = true;

        // --- Optimized Distribution Loop ---
        while (remainingMemberInfos.Count > 0) // Loop while members remain unplaced
        {
            // Find the next *sequential* member that hasn't been placed yet
            MemberDeclarationSyntax? nextSequentialMember = null;
            MemberInfo? nextSequentialMemberInfo = null;

            // Find the first member from original order that is still in remainingMemberInfos
            foreach(MemberDeclarationSyntax member in allMembersInOrder)
            {
                 if (remainingMemberInfos.ContainsKey(member))
                 {
                     nextSequentialMember = member;
                     nextSequentialMemberInfo = remainingMemberInfos[member];
                     break;
                 }
            }

            if (nextSequentialMember == null || nextSequentialMemberInfo == null)
            {
                Console.WriteLine("  Error: No remaining sequential member found, but remaining count > 0. Exiting distribution loop.");
                break; // Should not happen
            }
            string nextSequentialMemberName = GetMemberName(nextSequentialMember);

            // --- Test adding the sequential member ---
            int locAddingSequential = CountLines(GenerateFileContent(workspace, usings, namespaceSyntax, originalClassDeclaration, [.. currentFileMembers, nextSequentialMember], processingOriginalFile, baseOverheadLoc));

            // --- Case 1: Sequential member fits ---
            if (locAddingSequential <= maxLinesPerFile)
            {
                // Console.WriteLine($"    -> Adding sequential member '{nextSequentialMemberName}' (Est: {nextSequentialMemberInfo.Value.EstimatedLoc}, New LOC: {locAddingSequential})"); // Verbose
                currentFileMembers.Add(nextSequentialMember);
                currentFileLoc = locAddingSequential;

                // Remove from tracking
                bool removedFromDict = remainingMemberInfos.Remove(nextSequentialMember);
                bool removedFromList = remainingMembersSortedByLoc.Remove(nextSequentialMemberInfo.Value);
                Debug.Assert(removedFromDict && removedFromList, "Sequential member should have been present in tracking collections");
            }
            // --- Case 2: Sequential member does NOT fit ---
            else
            {
                // --- Subcase 2a: Try to find a filler ---
                int remainingSpace = maxLinesPerFile - currentFileLoc;
                MemberInfo? fillerInfo = FindLargestFittingMember(remainingMembersSortedByLoc, nextSequentialMemberInfo.Value, remainingSpace, baseOverheadLoc);

                bool fillerAdded = false;
                if (fillerInfo.HasValue)
                {
                    MemberDeclarationSyntax fillerMember = fillerInfo.Value.Member;
                    string fillerMemberName = GetMemberName(fillerMember);
                    // Actual check
                    int locAddingFiller = CountLines(GenerateFileContent(workspace, usings, namespaceSyntax, originalClassDeclaration, [.. currentFileMembers, fillerMember], processingOriginalFile, baseOverheadLoc));

                    if (locAddingFiller <= maxLinesPerFile)
                    {
                        // Filler fits! Add it.
                        currentFileMembers.Add(fillerMember);
                        currentFileLoc = locAddingFiller;

                        // Remove filler from tracking
                        bool removedFromDict = remainingMemberInfos.Remove(fillerMember);
                        bool removedFromList = remainingMembersSortedByLoc.Remove(fillerInfo.Value);
                        Debug.Assert(removedFromDict && removedFromList, "Filler member should have been present in tracking collections");

                        Console.WriteLine($"    -> Sequential member '{nextSequentialMemberName}' (est. {nextSequentialMemberInfo.Value.EstimatedLoc} LOC) didn't fit. Added smaller filler member '{fillerMemberName}' (est. {fillerInfo.Value.EstimatedLoc} LOC). New file LOC: {currentFileLoc}");
                        fillerAdded = true;
                    }
                    else
                    {
                         Console.WriteLine($"    -> Candidate filler member '{fillerMemberName}' (est. {fillerInfo.Value.EstimatedLoc} LOC) did not actually fit (would be {locAddingFiller} LOC).");
                         // Proceed to split (handled below)
                    }
                }

                // --- Subcase 2b: No filler added (none found or actual check failed) ---
                if (!fillerAdded)
                {
                    Console.WriteLine($"    -> Sequential member '{nextSequentialMemberName}' (est. {nextSequentialMemberInfo.Value.EstimatedLoc} LOC) didn't fit (would be {locAddingSequential} LOC). No suitable filler found. Splitting file.");

                    // Finalize the current file (only if it contains members)
                    if (currentFileMembers.Count > 0)
                    {
                         FinalizeCurrentFile(processingOriginalFile, currentFileMembers, membersToKeepInOriginal, newFilesMembers);
                    }

                    // Start the new file with the sequential member that didn't fit
                    processingOriginalFile = false; // Now processing subsequent files
                    currentFileMembers = [nextSequentialMember]; // Reset list for the new file
                    currentFileLoc = CountLines(GenerateFileContent(workspace, usings, namespaceSyntax, originalClassDeclaration, currentFileMembers, false, baseOverheadLoc)); // Calculate LOC for the new file

                    // Check edge case: single member too large for new file
                    if (currentFileLoc > maxLinesPerFile)
                    {
                        Console.WriteLine($"  Warning: Member '{nextSequentialMemberName}' (est. {nextSequentialMemberInfo.Value.EstimatedLoc} LOC) is too large ({currentFileLoc} lines with overhead) for a new file. Placing it in its own oversized file.");
                        // Finalize this single-member oversized file immediately
                        FinalizeCurrentFile(false, currentFileMembers, membersToKeepInOriginal, newFilesMembers);
                        currentFileMembers = []; // Reset list, ready for the *next* sequential member
                        currentFileLoc = baseOverheadLoc; // Reset LOC for the (now empty) next potential file
                    }
                    // Else: Member fits in the new file, currentFileMembers/Loc are correctly set for the next iteration.

                    // Remove the sequential member from tracking as it's now placed
                    bool removedFromDict = remainingMemberInfos.Remove(nextSequentialMember);
                    bool removedFromList = remainingMembersSortedByLoc.Remove(nextSequentialMemberInfo.Value);
                    Debug.Assert(removedFromDict && removedFromList, "Sequential member (that caused split) should have been present in tracking collections");
                }
            }
        } // End while loop

        // Add the very last batch of members if any remain after the loop
        if (currentFileMembers.Count > 0)
        {
            FinalizeCurrentFile(processingOriginalFile, currentFileMembers, membersToKeepInOriginal, newFilesMembers);
        }


        // --- File Generation ---
        if (newFilesMembers.Count == 0 && membersToKeepInOriginal.Count == originalClassDeclaration.Members.Count)
        {
            // This case should have been caught by the initial check, but safety first.
            Console.WriteLine($"  Splitting resulted in no changes for '{Path.GetFileName(filePath)}'. Original file remains as is.");
            return false; // No modification occurred
        }
         if (newFilesMembers.Count == 0 && membersToKeepInOriginal.Count < originalClassDeclaration.Members.Count)
         {
             Console.WriteLine($"  Splitting resulted in only modifying the original file '{Path.GetFileName(filePath)}'.");
             // Proceed to modify original, but don't create new files.
         }


        // Modify the original file
        await ModifyOriginalClassToPartialAsync(workspace, filePath, root, usings, namespaceSyntax, originalClassDeclaration, membersToKeepInOriginal, baseOverheadLoc);

        // Create new partial class files
        if (newFilesMembers.Count > 0)
        {
            int startNumber = FindNextAvailableNumber(directory, fileName, extension);
            Console.WriteLine($"  Generating {newFilesMembers.Count} new partial file(s) starting from number {startNumber}...");
            for (int i = 0; i < newFilesMembers.Count; i++)
            {
                int fileNumber = startNumber + i;
                string partialFileName = $"{fileName}{fileNumber}{extension}";
                string partialFilePath = Path.Combine(directory, partialFileName);
                List<MemberDeclarationSyntax> membersForThisFile = newFilesMembers[i];
                List<string> memberNames = membersForThisFile.Select(GetMemberName).ToList();

                string fileContent = GenerateFileContent(workspace, usings, namespaceSyntax, originalClassDeclaration, membersForThisFile, false, baseOverheadLoc); // isOriginal=false
                int lineCount = CountLines(fileContent);

                if (lineCount > maxLinesPerFile)
                {
                    Console.WriteLine($"  Warning: Generated file '{partialFileName}' has {lineCount} lines, exceeding the limit of {maxLinesPerFile}. Members: {string.Join(", ", memberNames)}");
                }

                await File.WriteAllTextAsync(partialFilePath, fileContent);
                Console.WriteLine($"  Created file: '{partialFileName}' with {membersForThisFile.Count} members ({lineCount} lines)."); // Members: {string.Join(", ", memberNames)}"); // Optionally list members
            }
            Console.WriteLine($"  Class '{className}' split into {newFilesMembers.Count + 1} files (original + {newFilesMembers.Count} new).");
        }
        else {
             Console.WriteLine($"  Original file '{Path.GetFileName(filePath)}' modified. No new files created.");
        }
        return true; // File was modified or split
    }

    // --- Helper Methods ---

    /// <summary>
    /// Gets a descriptive name for a member declaration.
    /// </summary>
    private static string GetMemberName(MemberDeclarationSyntax member)
    {
        return member switch
        {
            MethodDeclarationSyntax m => $"{m.Identifier.Text}()",
            PropertyDeclarationSyntax p => p.Identifier.Text,
            FieldDeclarationSyntax f => string.Join(", ", f.Declaration.Variables.Select(v => v.Identifier.Text)),
            ConstructorDeclarationSyntax c => $"{c.Identifier.Text}()",
            DestructorDeclarationSyntax d => $"{d.Identifier.Text}()",
            EventFieldDeclarationSyntax ef => string.Join(", ", ef.Declaration.Variables.Select(v => v.Identifier.Text)),
            IndexerDeclarationSyntax i => $"{i.ThisKeyword.Text}[]",
            OperatorDeclarationSyntax o => $"operator {o.OperatorToken.Text}",
            ConversionOperatorDeclarationSyntax co => $"operator {co.Type}", // Implicit/Explicit handled by keyword
            ClassDeclarationSyntax nestedClass => $"class {nestedClass.Identifier.Text}",
            StructDeclarationSyntax nestedStruct => $"struct {nestedStruct.Identifier.Text}",
            InterfaceDeclarationSyntax nestedInterface => $"interface {nestedInterface.Identifier.Text}",
            EnumDeclarationSyntax nestedEnum => $"enum {nestedEnum.Identifier.Text}",
            DelegateDeclarationSyntax del => $"delegate {del.Identifier.Text}",
            _ => member.Kind().ToString() // Fallback
        };
    }


    /// <summary>
    /// Helper to finalize the current list of members, adding it either to the
    /// original list or the list of new files.
    /// </summary>
    private static void FinalizeCurrentFile(
        bool isOriginal,
        List<MemberDeclarationSyntax> currentMembers,
        List<MemberDeclarationSyntax> originalFileMembersTarget,
        List<List<MemberDeclarationSyntax>> newFilesTarget)
    {
        if (isOriginal)
        {
            Debug.Assert(originalFileMembersTarget.Count == 0, "Original file members should only be assigned once.");
            originalFileMembersTarget.AddRange(currentMembers);
            // Console.WriteLine($"    -> Finalizing original file part with {currentMembers.Count} members."); // Verbose
        }
        else
        {
            newFilesTarget.Add(new List<MemberDeclarationSyntax>(currentMembers)); // Add a copy
            // Console.WriteLine($"    -> Finalizing new file part #{newFilesTarget.Count} with {currentMembers.Count} members."); // Verbose
        }
    }


    /// <summary>
    /// Finds the largest member (by estimated LOC) in the sorted list that might fit
    /// within the remaining space, excluding the specified memberToExclude.
    /// Uses the *estimated* LOC for candidate selection.
    /// </summary>
    private static MemberInfo? FindLargestFittingMember(
        List<MemberInfo> currentSortedRemainingMembers,
        MemberInfo memberToExclude,
        int remainingLineSpace,
        int baseOverheadLoc) // Pass base overhead for this file
    {
        MemberInfo? bestFit = null;

        // Iterate downwards from the end of the *current* sorted list of *remaining* members
        for (int i = currentSortedRemainingMembers.Count - 1; i >= 0; i--)
        {
            MemberInfo candidate = currentSortedRemainingMembers[i];

            // Skip the member we are trying *not* to add sequentially right now
            if (candidate.Member == memberToExclude.Member)
            {
                continue;
            }

            // Estimate the increase in lines this candidate would cause.
            // This is the candidate's total estimated lines minus the base overhead.
            // Ensure increase is at least 1 line for the member itself.
            int estimatedIncrease = Math.Max(1, candidate.EstimatedLoc - baseOverheadLoc);

            // Check if the estimated *increase* fits within the available space
            if (estimatedIncrease <= remainingLineSpace)
            {
                // This is the largest fitting candidate found so far (due to iteration order)
                bestFit = candidate;
                break; // Found the best (largest fitting) one
            }
        }

        return bestFit;
    }

    /// <summary>
    /// Calculates the LOC of an empty partial class file with boilerplate for the *current* file context.
    /// </summary>
     private static int CalculateOverheadLoc(
        Workspace workspace,
        SyntaxList<UsingDirectiveSyntax> usings,
        BaseNamespaceDeclarationSyntax? namespaceSyntax,
        ClassDeclarationSyntax originalClassDeclaration)
     {
         // Calculate overhead for the specific context
         string emptyContent = GenerateFileContent(workspace, usings, namespaceSyntax, originalClassDeclaration, [], false, 0); // Pass 0 for baseOverhead initially
         return CountLines(emptyContent);
     }


    /// <summary>
    /// Generates the C# code content for a partial class file.
    /// </summary>
    private static string GenerateFileContent(
        Workspace workspace,
        SyntaxList<UsingDirectiveSyntax> usings,
        BaseNamespaceDeclarationSyntax? namespaceSyntax,
        ClassDeclarationSyntax originalClassDeclaration,
        List<MemberDeclarationSyntax> members,
        bool isOriginalFileModification,
        int baseOverheadLoc) // Accept baseOverheadLoc (though not directly used in generation itself)
    {
        // Create the partial class node
        ClassDeclarationSyntax partialClass = CreatePartialClassDeclaration(originalClassDeclaration, members, isOriginalFileModification);

        // Handle namespace structure (File-scoped or Block-scoped)
        MemberDeclarationSyntax topLevelMember;
        if (namespaceSyntax is FileScopedNamespaceDeclarationSyntax fileScopedNamespace)
        {
            // Ensure semicolon has appropriate trailing trivia (newlines)
            SyntaxToken semicolonWithTrivia = fileScopedNamespace.SemicolonToken
                .WithTrailingTrivia(SyntaxFactory.ElasticEndOfLine(Environment.NewLine), SyntaxFactory.ElasticEndOfLine(Environment.NewLine)); // Add extra line after namespace ;
            topLevelMember = fileScopedNamespace
                .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(partialClass))
                .WithSemicolonToken(semicolonWithTrivia);
        }
        else if (namespaceSyntax is NamespaceDeclarationSyntax blockNamespace)
        {
            // Ensure braces have appropriate trivia (newlines)
             SyntaxToken openBraceWithTrivia = blockNamespace.OpenBraceToken
                .WithTrailingTrivia(SyntaxFactory.ElasticEndOfLine(Environment.NewLine));
             SyntaxToken closeBraceWithTrivia = blockNamespace.CloseBraceToken
                .WithLeadingTrivia(SyntaxFactory.ElasticEndOfLine(Environment.NewLine)); // Newline before closing brace
            topLevelMember = blockNamespace
                .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(partialClass))
                .WithOpenBraceToken(openBraceWithTrivia)
                .WithCloseBraceToken(closeBraceWithTrivia);
        }
        else // No namespace
        {
             topLevelMember = partialClass;
        }

        // Build the compilation unit
        CompilationUnitSyntax compilationUnit = SyntaxFactory.CompilationUnit();

        // Add Usings with appropriate spacing
        if (usings.Count > 0)
        {
            List<SyntaxTrivia> triviaList = new List<SyntaxTrivia> { SyntaxFactory.ElasticEndOfLine(Environment.NewLine) };
            // Add an extra blank line between usings and the first member (namespace or class)
            if (namespaceSyntax != null || topLevelMember is ClassDeclarationSyntax)
            {
                triviaList.Add(SyntaxFactory.ElasticEndOfLine(Environment.NewLine));
            }

            // Apply trivia to the last using directive
            UsingDirectiveSyntax lastUsing = usings.Last();
            UsingDirectiveSyntax lastUsingWithTrivia = lastUsing.WithTrailingTrivia(triviaList);
            compilationUnit = compilationUnit.WithUsings(usings.Replace(lastUsing, lastUsingWithTrivia));
        }

        // Add the main member (namespace or class)
        compilationUnit = compilationUnit.AddMembers(topLevelMember);

        // Format the code
        SyntaxNode formattedNode = Formatter.Format(compilationUnit, workspace, workspace.Options);
        return formattedNode.ToFullString();
    }

    /// <summary>
    /// Creates a ClassDeclarationSyntax node, ensuring it has the 'partial' modifier.
    /// </summary>
    private static ClassDeclarationSyntax CreatePartialClassDeclaration(
        ClassDeclarationSyntax originalClassDeclaration,
        List<MemberDeclarationSyntax> members,
        bool isOriginalFileModification) // Parameter kept for potential future use, currently unused in logic
    {
        SyntaxTokenList modifiers = originalClassDeclaration.Modifiers;
        bool needsPartialKeyword = !modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword));

        if (needsPartialKeyword)
        {
            // Create the partial keyword with a trailing space
            SyntaxToken partialKeyword = SyntaxFactory.Token(SyntaxFactory.TriviaList(), SyntaxKind.PartialKeyword, SyntaxFactory.TriviaList(SyntaxFactory.Space));

            // Determine the correct insertion index for 'partial' (usually after access modifiers)
            int insertIndex = 0;
            SyntaxKind[] accessModifiers = new[] { SyntaxKind.PublicKeyword, SyntaxKind.InternalKeyword, SyntaxKind.ProtectedKeyword, SyntaxKind.PrivateKeyword };
            SyntaxKind[] otherModifiers = new[] { SyntaxKind.StaticKeyword, SyntaxKind.AbstractKeyword, SyntaxKind.SealedKeyword, SyntaxKind.ReadOnlyKeyword /* for record struct */, SyntaxKind.RefKeyword /* for ref struct */ }; // Add more as needed

            SyntaxToken lastAccess = modifiers.LastOrDefault(m => accessModifiers.Contains(m.Kind()));
            SyntaxToken lastOther = modifiers.LastOrDefault(m => otherModifiers.Contains(m.Kind())); // Consider other keywords like static, abstract, sealed

            if (!lastAccess.IsKind(SyntaxKind.None)) // If access modifier exists
            {
                insertIndex = modifiers.IndexOf(lastAccess) + 1;
            }
            else if (!lastOther.IsKind(SyntaxKind.None)) // If other relevant modifier exists (and no access modifier)
            {
                 insertIndex = modifiers.IndexOf(lastOther) + 1;
            }
            // Else: insert at the beginning (index 0)

            modifiers = modifiers.Insert(insertIndex, partialKeyword);
        }

        // Ensure proper spacing and newlines around braces
        SyntaxToken openBrace = originalClassDeclaration.OpenBraceToken
            .WithLeadingTrivia(SyntaxFactory.TriviaList(SyntaxFactory.Space)) // Space before {
            .WithTrailingTrivia(SyntaxFactory.TriviaList(SyntaxFactory.ElasticEndOfLine(Environment.NewLine))); // Newline after {

        SyntaxToken closeBrace = originalClassDeclaration.CloseBraceToken
             .WithLeadingTrivia(SyntaxFactory.TriviaList(SyntaxFactory.ElasticEndOfLine(Environment.NewLine))); // Newline before }

        // Return the modified class declaration
        return originalClassDeclaration
            .WithModifiers(modifiers)
            .WithMembers(SyntaxFactory.List(members)) // Set the members for this partial file
            .WithOpenBraceToken(openBrace)
            .WithCloseBraceToken(closeBrace)
            // Clear attributes/base list etc. for subsequent partial files? No, keep them for context.
            // Keep original attributes, base list, constraints etc. on all parts.
            .WithAttributeLists(originalClassDeclaration.AttributeLists)
            .WithBaseList(originalClassDeclaration.BaseList)
            .WithTypeParameterList(originalClassDeclaration.TypeParameterList)
            .WithConstraintClauses(originalClassDeclaration.ConstraintClauses);
    }

    /// <summary>
    /// Modifies the original file to contain only the specified members and the 'partial' keyword.
    /// </summary>
    private static async Task ModifyOriginalClassToPartialAsync(
        Workspace workspace,
        string filePath,
        CompilationUnitSyntax root, // Pass root for context if needed, though GenerateFileContent rebuilds
        SyntaxList<UsingDirectiveSyntax> usings,
        BaseNamespaceDeclarationSyntax? namespaceSyntax,
        ClassDeclarationSyntax originalClassDeclaration,
        List<MemberDeclarationSyntax> membersToKeep,
        int baseOverheadLoc) // Pass base overhead
    {
        string finalContent = GenerateFileContent(workspace, usings, namespaceSyntax, originalClassDeclaration, membersToKeep, true, baseOverheadLoc);
        int lineCount = CountLines(finalContent);
        string fileNameOnly = Path.GetFileName(filePath);

        try
        {
            await File.WriteAllTextAsync(filePath, finalContent);
            Console.WriteLine($"  Modified original file: '{fileNameOnly}' with {membersToKeep.Count} members ({lineCount} lines).");

             // Use the static field holding the parsed max lines value
             if (membersToKeep.Count > 0 && lineCount > _maxLinesPerFile) // Only warn if not empty and exceeding
             {
                  Console.WriteLine($"  Warning: Modified original file '{fileNameOnly}' still exceeds the line limit ({lineCount} > {_maxLinesPerFile}).");
             }
             else if (membersToKeep.Count == 0)
             {
                  Console.WriteLine($"  Note: Original file '{fileNameOnly}' is now empty except for the partial class definition.");
             }
        }
        catch (IOException ex)
        {
             Console.WriteLine($"  Error writing modified original file '{fileNameOnly}': {ex.Message}");
        }
    }

    /// <summary>
    /// Counts lines in a string, normalizing line endings.
    /// </summary>
    private static int CountLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int lineCount = 0;
        int index = -1;
        while ((index = text.IndexOf('\n', index + 1)) != -1)
        {
            lineCount++;
        }
        // Add 1 for the last line if the text is not empty
        return lineCount + (text.Length > 0 ? 1 : 0);
    }


    /// <summary>
    /// Finds the next available number N for filenames like "BaseNameN.ext".
    /// </summary>
    private static int FindNextAvailableNumber(string directory, string fileName, string extension)
    {
        // Regex to match BaseName followed by digits and the extension
        // Ensures it matches the *entire* filename to avoid partial matches (e.g., BaseNameExtended1.cs)
        Regex regex = new Regex($@"^{Regex.Escape(fileName)}(\d+){Regex.Escape(extension)}$", RegexOptions.IgnoreCase);
        int highestNumber = 1; // Start checking from 2 (so first new file is BaseName2.cs)

        try
        {
            // Enumerate files matching the pattern BaseName*Extension
            IEnumerable<string> existingFiles = Directory.EnumerateFiles(directory, $"{fileName}*{extension}");
            foreach (string file in existingFiles)
            {
                string nameOnly = Path.GetFileName(file);
                Match match = regex.Match(nameOnly);
                if (match.Success && int.TryParse(match.Groups[1].Value, out int number))
                {
                    highestNumber = Math.Max(highestNumber, number);
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            // This shouldn't happen if the input file existed, but handle defensively
            Console.WriteLine($"  Warning: Directory not found while checking existing file numbers: {directory}");
            return 2; // Default to 2 if directory disappears
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
             Console.WriteLine($"  Warning: Could not access directory to check existing file numbers: {ex.Message}");
             // We might overwrite files if we can't check, but proceed cautiously.
             return highestNumber + 1;
        }

        // Return the next number after the highest one found
        return highestNumber + 1;
    }
}