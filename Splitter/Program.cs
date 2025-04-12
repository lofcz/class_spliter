using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using System.Diagnostics; // For Debug.Assert

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

    // Static variable to cache base overhead LOC
    private static int _baseOverheadLoc = -1;


    private static void Main(string[] args)
    {
        // --- Argument Parsing ---
        if (args.Length < 1)
        {
            Console.WriteLine($"Usage: ClassSplitter.exe <filepath> [maxlines={DefaultMaxLines}]");
            return;
        }

        string filePath = args[0];
        int parsedMaxLines = DefaultMaxLines; // Use temporary variable for parsing

        var maxLinesArg = args.FirstOrDefault(a => a.StartsWith("maxlines=", StringComparison.OrdinalIgnoreCase));
        if (maxLinesArg != null)
        {
            if (int.TryParse(maxLinesArg.Split('=')[1], out int val) && val > 0)
            {
                parsedMaxLines = val;
            }
            else
            {
                Console.WriteLine($"Invalid maxlines value: {maxLinesArg.Split('=')[1]}. Using default: {DefaultMaxLines}");
            }
        }
        else if (args.Length > 1) // Support positional argument
        {
             if (int.TryParse(args[1], out int val) && val > 0)
             {
                 parsedMaxLines = val;
             }
             else
             {
                 Console.WriteLine($"Invalid maxlines value: {args[1]}. Using default: {DefaultMaxLines}");
             }
        }
        _maxLinesPerFile = parsedMaxLines; // Set the global static variable


        if (!File.Exists(filePath))
        {
            Console.WriteLine($"File {filePath} not found.");
            return;
        }

        Console.WriteLine($"Processing {filePath} with max lines per file: {_maxLinesPerFile}");
        SplitClass(filePath, _maxLinesPerFile);
    }

    private static void SplitClass(string filePath, int maxLinesPerFile) // Keep parameter for clarity
    {
        string sourceCode = File.ReadAllText(filePath);
        string fileName = Path.GetFileNameWithoutExtension(filePath);
        string extension = Path.GetExtension(filePath);
        string directory = Path.GetDirectoryName(filePath) ?? ".";

        // --- Parsing and Validation ---
        SyntaxTree tree = CSharpSyntaxTree.ParseText(sourceCode, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest));
        CompilationUnitSyntax root = tree.GetCompilationUnitRoot();
        var diagnostics = tree.GetDiagnostics();
        if (diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            Console.WriteLine("Error parsing file. Please fix syntax errors:");
            foreach (var diag in diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)) Console.WriteLine($"- {diag}");
            return;
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
            Console.WriteLine("No class declaration found in the file.");
            return;
        }

        int initialLineCount = CountLines(sourceCode);
        if (initialLineCount <= maxLinesPerFile)
        {
            Console.WriteLine($"Class '{originalClassDeclaration.Identifier.Text}' doesn't need to be split ({initialLineCount} lines <= {maxLinesPerFile}).");
            return;
        }
        Console.WriteLine($"Initial class '{originalClassDeclaration.Identifier.Text}' has {initialLineCount} lines, exceeding limit of {maxLinesPerFile}. Splitting...");


        // --- Pre-calculate Member LOC & Setup Tracking ---
        using var workspace = new AdhocWorkspace();
        List<MemberInfo> allMemberInfos = [];
        // Calculate and cache base overhead LOC
        _baseOverheadLoc = CalculateOverheadLoc(workspace, usings, namespaceSyntax, originalClassDeclaration);

        Console.WriteLine("Calculating estimated LOC for each member...");
        foreach (MemberDeclarationSyntax memberSyntax in originalClassDeclaration.Members)
        {
            // Estimate LOC by generating a file with only this member + overhead
            string memberContent = GenerateFileContent(workspace, usings, namespaceSyntax, originalClassDeclaration, [memberSyntax], false);
            int memberTotalLoc = CountLines(memberContent);
            // Ensure estimate is at least base overhead + 1 line for the member itself.
            int estimatedLoc = Math.Max(_baseOverheadLoc + 1, memberTotalLoc);
            allMemberInfos.Add(new MemberInfo(estimatedLoc, memberSyntax));

            if (memberTotalLoc > maxLinesPerFile)
            {
                 Console.WriteLine($"Warning: Member starting near line {memberSyntax.GetLocation().GetLineSpan().StartLinePosition.Line + 1} is estimated to be too large ({memberTotalLoc} lines with overhead) to fit within the {maxLinesPerFile} line limit even in its own file.");
            }
        }
        Console.WriteLine($"Calculated LOC for {allMemberInfos.Count} members.");

        List<MemberDeclarationSyntax> allMembersInOrder = originalClassDeclaration.Members.ToList();
        List<List<MemberDeclarationSyntax>> newFilesMembers = [];
        List<MemberDeclarationSyntax> membersToKeepInOriginal = [];
        List<MemberDeclarationSyntax> currentFileMembers = [];

        // Track remaining members efficiently
        var remainingMemberInfos = allMemberInfos.ToDictionary(info => info.Member, info => info);
        // Keep a separate list sorted by LOC for finding fillers
        var remainingMembersSortedByLoc = allMemberInfos.OrderBy(m => m).ToList();

        int currentFileLoc = _baseOverheadLoc; // Start with overhead
        bool processingOriginalFile = true;

        // --- Optimized Distribution Loop ---
        while (remainingMemberInfos.Count > 0) // Loop while members remain unplaced
        {
            // Find the next *sequential* member that hasn't been placed yet
            MemberDeclarationSyntax? nextSequentialMember = null;
            MemberInfo? nextSequentialMemberInfo = null;

            // Find the first member from original order that is still in remainingMemberInfos
            // This ensures we process members sequentially unless we add a filler
            foreach(var member in allMembersInOrder)
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
                // Should not happen if remainingMemberInfos.Count > 0, but safety check
                Console.WriteLine("Error: No remaining sequential member found, but remaining count > 0. Exiting loop.");
                break;
            }

            // --- Test adding the sequential member ---
            int locAddingSequential = CountLines(GenerateFileContent(workspace, usings, namespaceSyntax, originalClassDeclaration, [.. currentFileMembers, nextSequentialMember], processingOriginalFile));

            // --- Case 1: Sequential member fits ---
            if (locAddingSequential <= maxLinesPerFile)
            {
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
                MemberInfo? fillerInfo = FindLargestFittingMember(remainingMembersSortedByLoc, nextSequentialMemberInfo.Value, remainingSpace); // Pass necessary info

                bool fillerAdded = false;
                if (fillerInfo.HasValue)
                {
                    var fillerMember = fillerInfo.Value.Member;
                    // Actual check
                    int locAddingFiller = CountLines(GenerateFileContent(workspace, usings, namespaceSyntax, originalClassDeclaration, [.. currentFileMembers, fillerMember], processingOriginalFile));

                    if (locAddingFiller <= maxLinesPerFile)
                    {
                        // Filler fits! Add it.
                        currentFileMembers.Add(fillerMember);
                        currentFileLoc = locAddingFiller;

                        // Remove filler from tracking
                        bool removedFromDict = remainingMemberInfos.Remove(fillerMember);
                        bool removedFromList = remainingMembersSortedByLoc.Remove(fillerInfo.Value);
                        Debug.Assert(removedFromDict && removedFromList, "Filler member should have been present in tracking collections");

                        Console.WriteLine($"    -> Sequential member (est. {nextSequentialMemberInfo.Value.EstimatedLoc} LOC) didn't fit. Added smaller filler member (est. {fillerInfo.Value.EstimatedLoc} LOC).");
                        fillerAdded = true;
                        // Loop continues, the original sequential member (nextSequentialMember) is still in remainingMemberInfos
                        // and will be considered again in the next iteration of the while loop.
                    }
                    else
                    {
                         Console.WriteLine($"    -> Candidate filler member (est. {fillerInfo.Value.EstimatedLoc} LOC) did not actually fit (would be {locAddingFiller} LOC).");
                         // Proceed to split (handled below)
                    }
                }

                // --- Subcase 2b: No filler added (none found or actual check failed) ---
                if (!fillerAdded)
                {
                    Console.WriteLine($"    -> Sequential member (est. {nextSequentialMemberInfo.Value.EstimatedLoc} LOC) didn't fit. No other suitable member found. Splitting.");

                    // Finalize the current file (only if it contains members)
                    if (currentFileMembers.Count > 0)
                    {
                         FinalizeCurrentFile(processingOriginalFile, currentFileMembers, membersToKeepInOriginal, newFilesMembers);
                    }
                    // If processingOriginalFile is true and currentFileMembers is empty, it means
                    // the very first member didn't fit, so membersToKeepInOriginal remains empty, which is correct.

                    // Start the new file with the sequential member that didn't fit
                    processingOriginalFile = false; // Now processing subsequent files
                    currentFileMembers = [nextSequentialMember]; // Reset list for the new file
                    currentFileLoc = CountLines(GenerateFileContent(workspace, usings, namespaceSyntax, originalClassDeclaration, currentFileMembers, false)); // Calculate LOC for the new file

                    // Check edge case: single member too large for new file
                    if (currentFileLoc > maxLinesPerFile)
                    {
                        Console.WriteLine($"Warning: Member starting near line {nextSequentialMember.GetLocation().GetLineSpan().StartLinePosition.Line + 1} (est. {nextSequentialMemberInfo.Value.EstimatedLoc} LOC) is too large ({currentFileLoc} lines with overhead) for a new file. Placing it in its own oversized file.");
                        // Finalize this single-member oversized file immediately
                        FinalizeCurrentFile(false, currentFileMembers, membersToKeepInOriginal, newFilesMembers);
                        currentFileMembers = []; // Reset list, ready for the *next* sequential member
                        currentFileLoc = _baseOverheadLoc; // Reset LOC for the (now empty) next potential file
                    }
                    // Else: Member fits in the new file, currentFileMembers/Loc are correctly set for the next iteration.

                    // Remove the sequential member from tracking as it's now placed (either in the new file or its own oversized one)
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
            Console.WriteLine("Splitting resulted in no changes. Original file remains as is.");
            return;
        }
         if (newFilesMembers.Count == 0 && membersToKeepInOriginal.Count < originalClassDeclaration.Members.Count)
        {
             // This means some members were removed but no new file created (e.g. they were too large?)
             // Or the logic decided to only keep a subset in the original.
             Console.WriteLine($"Splitting resulted in only modifying the original file.");
             // Proceed to modify original, but don't create new files.
         }


        // Modify the original file
        ModifyOriginalClassToPartial(workspace, filePath, root, usings, namespaceSyntax, originalClassDeclaration, membersToKeepInOriginal);

        // Create new partial class files
        if (newFilesMembers.Count > 0)
        {
            int startNumber = FindNextAvailableNumber(directory, fileName, extension);
            for (int i = 0; i < newFilesMembers.Count; i++)
            {
                int fileNumber = startNumber + i;
                string partialFileName = $"{fileName}{fileNumber}{extension}";
                string partialFilePath = Path.Combine(directory, partialFileName);
                List<MemberDeclarationSyntax> membersForThisFile = newFilesMembers[i];

                string fileContent = GenerateFileContent(workspace, usings, namespaceSyntax, originalClassDeclaration, membersForThisFile, false); // isOriginal=false
                int lineCount = CountLines(fileContent);

                if (lineCount > maxLinesPerFile)
                {
                    Console.WriteLine($"Warning: Generated file '{partialFilePath}' has {lineCount} lines, exceeding the limit of {maxLinesPerFile}. This likely means a single member plus overhead was too large.");
                }

                File.WriteAllText(partialFilePath, fileContent);
                Console.WriteLine($"Created file: {partialFileName} with {membersForThisFile.Count} members and {lineCount} lines.");
            }
             Console.WriteLine($"Class split into {newFilesMembers.Count + 1} files (original + {newFilesMembers.Count} new), numbered starting from {startNumber}.");
        }
        else {
             Console.WriteLine($"Original file modified. No new files created.");
        }
    }

    // --- Helper Methods ---

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
        // No need to check currentMembers.Count == 0 here,
        // the calling logic ensures it's only called when needed.

        if (isOriginal)
        {
            // Should only happen once for the very first file's members
            Debug.Assert(originalFileMembersTarget.Count == 0, "Original file members should only be assigned once.");
            originalFileMembersTarget.AddRange(currentMembers);
        }
        else
        {
            newFilesTarget.Add(new List<MemberDeclarationSyntax>(currentMembers)); // Add a copy
        }
        // DO NOT clear currentMembers here. The calling logic resets it when starting a new file.
    }


    /// <summary>
    /// Finds the largest member (by estimated LOC) in the sorted list that might fit
    /// within the remaining space, excluding the specified memberToExclude.
    /// Uses the *estimated* LOC for candidate selection.
    /// </summary>
    private static MemberInfo? FindLargestFittingMember(
        List<MemberInfo> currentSortedRemainingMembers, // Pass the current list state
        MemberInfo memberToExclude, // The sequential member that didn't fit
        int remainingLineSpace) // Space available BEFORE adding anything new
    {
        MemberInfo? bestFit = null;

        // Iterate downwards from the end of the *current* sorted list of *remaining* members
        for (int i = currentSortedRemainingMembers.Count - 1; i >= 0; i--)
        {
            var candidate = currentSortedRemainingMembers[i];

            // Skip the member we are trying *not* to add sequentially right now
            if (candidate.Member == memberToExclude.Member)
            {
                continue;
            }

            // Estimate the increase in lines this candidate would cause.
            // This is the candidate's total estimated lines minus the base overhead.
            int estimatedIncrease = Math.Max(1, candidate.EstimatedLoc - _baseOverheadLoc);

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
    /// Calculates the LOC of an empty partial class file with boilerplate. Caches the result.
    /// </summary>
     private static int CalculateOverheadLoc(
        Workspace workspace,
        SyntaxList<UsingDirectiveSyntax> usings,
        BaseNamespaceDeclarationSyntax? namespaceSyntax,
        ClassDeclarationSyntax originalClassDeclaration)
     {
         // Return cached value if available
         if (_baseOverheadLoc != -1) return _baseOverheadLoc;

         // Calculate otherwise
         string emptyContent = GenerateFileContent(workspace, usings, namespaceSyntax, originalClassDeclaration, [], false);
         _baseOverheadLoc = CountLines(emptyContent);
         return _baseOverheadLoc;
     }


    // --- GenerateFileContent (Unchanged from original) ---
    private static string GenerateFileContent(
        Workspace workspace,
        SyntaxList<UsingDirectiveSyntax> usings,
        BaseNamespaceDeclarationSyntax? namespaceSyntax,
        ClassDeclarationSyntax originalClassDeclaration,
        List<MemberDeclarationSyntax> members,
        bool isOriginalFileModification)
    {
        ClassDeclarationSyntax partialClass = CreatePartialClassDeclaration(originalClassDeclaration, members, isOriginalFileModification);
        MemberDeclarationSyntax topLevelMember;
        if (namespaceSyntax is FileScopedNamespaceDeclarationSyntax fileScopedNamespace) {
            var semicolonWithTrivia = fileScopedNamespace.SemicolonToken.WithTrailingTrivia(SyntaxFactory.ElasticEndOfLine(Environment.NewLine));
            topLevelMember = fileScopedNamespace.WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(partialClass)).WithSemicolonToken(semicolonWithTrivia);
        } else if (namespaceSyntax is NamespaceDeclarationSyntax blockNamespace) {
             var openBraceWithTrivia = blockNamespace.OpenBraceToken.WithTrailingTrivia(SyntaxFactory.ElasticEndOfLine(Environment.NewLine));
             var closeBraceWithTrivia = blockNamespace.CloseBraceToken.WithLeadingTrivia(SyntaxFactory.ElasticEndOfLine(Environment.NewLine));
            topLevelMember = blockNamespace.WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(partialClass)).WithOpenBraceToken(openBraceWithTrivia).WithCloseBraceToken(closeBraceWithTrivia);
        } else { topLevelMember = partialClass; }

        CompilationUnitSyntax compilationUnit = SyntaxFactory.CompilationUnit();
        if (usings.Count > 0) {
            var lastUsing = usings.Last();
            var triviaList = new List<SyntaxTrivia> { SyntaxFactory.ElasticEndOfLine(Environment.NewLine) };
            if (namespaceSyntax != null || members.Count > 0 || topLevelMember is ClassDeclarationSyntax) { triviaList.Add(SyntaxFactory.ElasticEndOfLine(Environment.NewLine)); }
            var lastUsingWithTrivia = lastUsing.WithTrailingTrivia(triviaList);
            compilationUnit = compilationUnit.WithUsings(usings.Replace(lastUsing, lastUsingWithTrivia));
        }
        compilationUnit = compilationUnit.AddMembers(topLevelMember);
        SyntaxNode formattedNode = Formatter.Format(compilationUnit, workspace);
        return formattedNode.ToFullString();
    }

    // --- CreatePartialClassDeclaration (Unchanged from original) ---
     private static ClassDeclarationSyntax CreatePartialClassDeclaration(
        ClassDeclarationSyntax originalClassDeclaration,
        List<MemberDeclarationSyntax> members,
        bool isOriginalFileModification)
    {
        SyntaxTokenList modifiers = originalClassDeclaration.Modifiers;
        bool needsPartialKeyword = !modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword));
        if (needsPartialKeyword) {
            SyntaxToken partialKeyword = SyntaxFactory.Token(SyntaxFactory.TriviaList(), SyntaxKind.PartialKeyword, SyntaxFactory.TriviaList(SyntaxFactory.Space));
            int insertIndex = 0;
             var accessModifiers = new[] { SyntaxKind.PublicKeyword, SyntaxKind.InternalKeyword, SyntaxKind.ProtectedKeyword, SyntaxKind.PrivateKeyword };
             var otherModifiers = new[] { SyntaxKind.StaticKeyword, SyntaxKind.AbstractKeyword, SyntaxKind.SealedKeyword };
             var lastAccess = modifiers.LastOrDefault(m => accessModifiers.Contains(m.Kind()));
             var lastOther = modifiers.LastOrDefault(m => otherModifiers.Contains(m.Kind()));
             if (lastAccess.IsKind(SyntaxKind.None) && lastOther.IsKind(SyntaxKind.None)) insertIndex = 0;
             else if (!lastAccess.IsKind(SyntaxKind.None) && lastOther.IsKind(SyntaxKind.None)) insertIndex = modifiers.IndexOf(lastAccess) + 1;
             else if (lastAccess.IsKind(SyntaxKind.None) && !lastOther.IsKind(SyntaxKind.None)) insertIndex = modifiers.IndexOf(lastOther) + 1;
             else insertIndex = Math.Max(modifiers.IndexOf(lastAccess), modifiers.IndexOf(lastOther)) + 1;
            modifiers = modifiers.Insert(insertIndex, partialKeyword);
        }
        SyntaxToken openBrace = originalClassDeclaration.OpenBraceToken.WithLeadingTrivia(SyntaxFactory.TriviaList(SyntaxFactory.Space)).WithTrailingTrivia(SyntaxFactory.TriviaList(SyntaxFactory.ElasticEndOfLine(Environment.NewLine)));
        SyntaxToken closeBrace = originalClassDeclaration.CloseBraceToken.WithLeadingTrivia(SyntaxFactory.TriviaList(SyntaxFactory.ElasticEndOfLine(Environment.NewLine)));
        return originalClassDeclaration.WithModifiers(modifiers).WithMembers(SyntaxFactory.List(members)).WithOpenBraceToken(openBrace).WithCloseBraceToken(closeBrace);
    }

    // --- ModifyOriginalClassToPartial (Now uses static _maxLinesPerFile) ---
    private static void ModifyOriginalClassToPartial(
        Workspace workspace,
        string filePath,
        CompilationUnitSyntax root,
        SyntaxList<UsingDirectiveSyntax> usings,
        BaseNamespaceDeclarationSyntax? namespaceSyntax,
        ClassDeclarationSyntax originalClassDeclaration,
        List<MemberDeclarationSyntax> membersToKeep)
    {
        // Ensure the list isn't empty if the original file should be empty (though unlikely)
        // if (membersToKeep.Count == 0) {
        //     Console.WriteLine("Warning: Original file is being emptied.");
        // }

        string finalContent = GenerateFileContent(workspace, usings, namespaceSyntax, originalClassDeclaration, membersToKeep, true);
        int lineCount = CountLines(finalContent);

        File.WriteAllText(filePath, finalContent);
        Console.WriteLine($"Modified original file: {Path.GetFileName(filePath)} with {membersToKeep.Count} members and {lineCount} lines.");
         // Use the static field holding the parsed max lines value
         if (membersToKeep.Count > 0 && lineCount > _maxLinesPerFile) // Only warn if not empty and exceeding
         {
              Console.WriteLine($"Warning: Modified original file still exceeds the line limit ({lineCount} > {_maxLinesPerFile}).");
         }
    }

    // --- CountLines (Unchanged from original) ---
    private static int CountLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        string normalizedText = text.Replace("\r\n", "\n").Replace("\r", "\n");
        return normalizedText.Split('\n').Length;
    }

    // --- FindNextAvailableNumber (Unchanged from original) ---
    private static int FindNextAvailableNumber(string directory, string fileName, string extension)
    {
        Regex regex = new Regex($@"^{Regex.Escape(fileName)}(\d+){Regex.Escape(extension)}$", RegexOptions.IgnoreCase);
        int highestNumber = 1;
        try {
            var existingFiles = Directory.EnumerateFiles(directory, $"{fileName}*{extension}");
            foreach (string file in existingFiles) {
                string nameOnly = Path.GetFileName(file);
                Match match = regex.Match(nameOnly);
                if (match.Success && int.TryParse(match.Groups[1].Value, out int number)) {
                    highestNumber = Math.Max(highestNumber, number);
                }
            }
        } catch (DirectoryNotFoundException) {
            Console.WriteLine($"Warning: Directory not found: {directory}"); return 2;
        }
        return highestNumber + 1;
    }
}