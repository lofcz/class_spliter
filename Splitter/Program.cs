using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
        int maxLinesPerFile = args.Length > 1 ? int.Parse(args[1]) : DefaultMaxLines;

        if (!File.Exists(filePath))
        {
            Console.WriteLine($"File {filePath} not found.");
            return;
        }

        SplitClass(filePath, maxLinesPerFile);
    }

    private static void SplitClass(string filePath, int maxLinesPerFile)
    {
        string sourceCode = File.ReadAllText(filePath);
        string fileName = Path.GetFileNameWithoutExtension(filePath);
        string extension = Path.GetExtension(filePath);
        string directory = Path.GetDirectoryName(filePath) ?? ".";
        int startNumber = FindNextAvailableNumber(directory, fileName, extension);
        
        SyntaxTree tree = CSharpSyntaxTree.ParseText(sourceCode);
        CompilationUnitSyntax root = tree.GetCompilationUnitRoot();
        SyntaxList<UsingDirectiveSyntax> usings = root.Usings;

        // namespace type
        ClassDeclarationSyntax? classDeclaration = null;
        bool isFileScopedNamespace = false;
        string? namespaceName = null;

        // namespace decl
        NamespaceDeclarationSyntax? namespaceDeclaration = root.Members.OfType<NamespaceDeclarationSyntax>().FirstOrDefault();
        
        if (namespaceDeclaration is not null)
        {
            classDeclaration = namespaceDeclaration.Members.OfType<ClassDeclarationSyntax>().FirstOrDefault();
            
            if (classDeclaration is not null)
            {
                namespaceName = namespaceDeclaration.Name.ToString();
            }
        }
        
        // file-scoped namespace decl
        FileScopedNamespaceDeclarationSyntax? fileScopedNamespace = root.Members.OfType<FileScopedNamespaceDeclarationSyntax>().FirstOrDefault();
        
        if (fileScopedNamespace is not null)
        {
            classDeclaration = fileScopedNamespace.Members.OfType<ClassDeclarationSyntax>().FirstOrDefault();
            
            if (classDeclaration is not null)
            {
                isFileScopedNamespace = true;
                namespaceName = fileScopedNamespace.Name.ToString();
            }
        }
        
        classDeclaration ??= root.Members.OfType<ClassDeclarationSyntax>().FirstOrDefault();

        if (classDeclaration is null)
        {
            Console.WriteLine("No class found.");
            return;
        }
        
        List<MemberDeclarationSyntax> allMembers = classDeclaration.Members.ToList();
        List<List<MemberDeclarationSyntax>> files = [];
        List<MemberDeclarationSyntax> currentFile = [];
        int currentLineCount = 0;
        int totalLineCount = 0;
        
        // Keep track of members to keep in the original file
        List<MemberDeclarationSyntax> membersToKeep = [];
        
        foreach (MemberDeclarationSyntax member in allMembers)
        {
            // get members
            FileLinePositionSpan lineSpan = member.GetLocation().GetLineSpan();
            int memberLineCount = lineSpan.EndLinePosition.Line - lineSpan.StartLinePosition.Line + 1;
            totalLineCount += memberLineCount;

            // Keep some members in the original file (first file)
            if (membersToKeep.Count == 0 || currentLineCount + memberLineCount <= maxLinesPerFile)
            {
                membersToKeep.Add(member);
                currentLineCount += memberLineCount;
            }
            else
            {
                // Start a new file for the remaining members
                if (currentFile.Count == 0)
                {
                    currentFile = [];
                }
                
                // If adding this member would exceed the limit for the current file,
                // add the current file to the list and start a new one
                if (currentFile.Count > 0 && currentFile.Sum(m => m.GetLocation().GetLineSpan().EndLinePosition.Line - m.GetLocation().GetLineSpan().StartLinePosition.Line + 1) + memberLineCount > maxLinesPerFile)
                {
                    files.Add(currentFile);
                    currentFile = [];
                }
                
                currentFile.Add(member);
            }
        }
        
        // Add the last file if it has members
        if (currentFile.Count > 0)
        {
            files.Add(currentFile);
        }

        // If we don't need to split, exit
        if (files.Count == 0)
        {
            Console.WriteLine($"Class doesn't need to be split (~{totalLineCount} lines).");
            return;
        }

        // Modify the original class to be partial and keep only the first set of members
        ModifyOriginalClassToPartial(filePath, classDeclaration, root, namespaceDeclaration, fileScopedNamespace, membersToKeep);

        // create partial classes for the remaining members
        for (int i = 0; i < files.Count; i++)
        {
            int fileNumber = startNumber + i;
            string partialFileName = $"{fileName}{fileNumber}{extension}";
            string partialFilePath = Path.Combine(directory, partialFileName);
            
            
            // Create a new partial token with leading and trailing trivia to ensure proper spacing
            SyntaxToken partialToken = SyntaxFactory.Token(
                SyntaxFactory.TriviaList(),
                SyntaxKind.PartialKeyword,
                SyntaxFactory.TriviaList(SyntaxFactory.Space));
                
            ClassDeclarationSyntax partialClass = classDeclaration
                .WithModifiers(SyntaxFactory.TokenList(
                    classDeclaration.Modifiers
                        .Where(m => !m.IsKind(SyntaxKind.PartialKeyword))
                        .Concat([partialToken])))
                .WithMembers(SyntaxFactory.List(files[i]));
            
            // first & last member formatting - remove leading/trailing newlines
            if (files[i].Count > 0)
            {
                MemberDeclarationSyntax firstMember = files[i][0];
                MemberDeclarationSyntax updatedFirstMember = firstMember.WithLeadingTrivia(SyntaxFactory.TriviaList(SyntaxFactory.Whitespace("    ")));
                
                MemberDeclarationSyntax lastMember = files[i][files[i].Count - 1];
                MemberDeclarationSyntax updatedLastMember = lastMember.WithTrailingTrivia(SyntaxFactory.TriviaList());

                List<MemberDeclarationSyntax> updatedMembers = new List<MemberDeclarationSyntax>(files[i])
                {
                    [0] = updatedFirstMember
                };
                
                if (files[i].Count == 1)
                {
                    updatedMembers[0] = updatedFirstMember.WithTrailingTrivia(SyntaxFactory.TriviaList());
                }
                else
                {
                    updatedMembers[files[i].Count - 1] = updatedLastMember;
                }

                partialClass = partialClass
                    .WithOpenBraceToken(SyntaxFactory.Token(
                        SyntaxFactory.TriviaList(),
                        SyntaxKind.OpenBraceToken,
                        SyntaxFactory.TriviaList(SyntaxFactory.LineFeed)))
                    .WithMembers(SyntaxFactory.List(updatedMembers))
                    .WithCloseBraceToken(SyntaxFactory.Token(
                        SyntaxFactory.TriviaList(SyntaxFactory.LineFeed),
                        SyntaxKind.CloseBraceToken,
                        SyntaxFactory.TriviaList()));
            }
            else
            {
                partialClass = partialClass
                    .WithOpenBraceToken(SyntaxFactory.Token(
                        SyntaxFactory.TriviaList(),
                        SyntaxKind.OpenBraceToken,
                        SyntaxFactory.TriviaList(SyntaxFactory.LineFeed)))
                    .WithCloseBraceToken(SyntaxFactory.Token(
                        SyntaxFactory.TriviaList(SyntaxFactory.LineFeed),
                        SyntaxKind.CloseBraceToken,
                        SyntaxFactory.TriviaList()));
            }
            
            CompilationUnitSyntax newCompilationUnit;

            if (isFileScopedNamespace)
            {
                FileScopedNamespaceDeclarationSyntax newFileScopedNamespace = SyntaxFactory.FileScopedNamespaceDeclaration(
                        SyntaxFactory.ParseName(namespaceName))
                    .WithNamespaceKeyword(SyntaxFactory.Token(
                        SyntaxFactory.TriviaList(),
                        SyntaxKind.NamespaceKeyword,
                        SyntaxFactory.TriviaList(SyntaxFactory.Space)))
                    .WithSemicolonToken(SyntaxFactory.Token(
                        SyntaxFactory.TriviaList(),
                        SyntaxKind.SemicolonToken,
                        SyntaxFactory.TriviaList(SyntaxFactory.LineFeed)))
                    .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(partialClass));

                newCompilationUnit = SyntaxFactory.CompilationUnit()
                    .WithUsings(usings)
                    .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(newFileScopedNamespace));
                
                if (usings.Count > 0)
                {
                    newCompilationUnit = newCompilationUnit.WithUsings(
                        newCompilationUnit.Usings.Replace(
                            newCompilationUnit.Usings.Last(),
                            newCompilationUnit.Usings.Last().WithTrailingTrivia(
                                SyntaxFactory.TriviaList(SyntaxFactory.LineFeed, SyntaxFactory.LineFeed))));
                }
            }
            else if (namespaceName is not null)
            {
                NamespaceDeclarationSyntax newNamespace = SyntaxFactory.NamespaceDeclaration(
                        SyntaxFactory.ParseName(namespaceName))
                    .WithNamespaceKeyword(SyntaxFactory.Token(
                        SyntaxFactory.TriviaList(),
                        SyntaxKind.NamespaceKeyword,
                        SyntaxFactory.TriviaList(SyntaxFactory.Space)))
                    .WithOpenBraceToken(SyntaxFactory.Token(
                        SyntaxFactory.TriviaList(),
                        SyntaxKind.OpenBraceToken,
                        SyntaxFactory.TriviaList(SyntaxFactory.LineFeed)))
                    .WithCloseBraceToken(SyntaxFactory.Token(
                        SyntaxFactory.TriviaList(),
                        SyntaxKind.CloseBraceToken,
                        SyntaxFactory.TriviaList()))
                    .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(partialClass));

                newCompilationUnit = SyntaxFactory.CompilationUnit()
                    .WithUsings(usings)
                    .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(newNamespace));
                
                if (usings.Count > 0)
                {
                    newCompilationUnit = newCompilationUnit.WithUsings(
                        newCompilationUnit.Usings.Replace(
                            newCompilationUnit.Usings.Last(),
                            newCompilationUnit.Usings.Last().WithTrailingTrivia(
                                SyntaxFactory.TriviaList(SyntaxFactory.LineFeed, SyntaxFactory.LineFeed))));
                }
            }
            else
            {
                // without namespace
                newCompilationUnit = SyntaxFactory.CompilationUnit()
                    .WithUsings(usings)
                    .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(partialClass));
                
                if (usings.Count > 0)
                {
                    newCompilationUnit = newCompilationUnit.WithUsings(
                        newCompilationUnit.Usings.Replace(
                            newCompilationUnit.Usings.Last(),
                            newCompilationUnit.Usings.Last().WithTrailingTrivia(
                                SyntaxFactory.TriviaList(SyntaxFactory.LineFeed, SyntaxFactory.LineFeed))));
                }
            }
            
            string fileContent = newCompilationUnit.ToFullString().Trim();
            File.WriteAllText(partialFilePath, fileContent);
            
            // count actual lines in the generated file
            int lineCount = fileContent.Split('\n').Length;
            Console.WriteLine($"Created file: {partialFilePath} with {files[i].Count} members and {lineCount} lines of code.");
        }

        Console.WriteLine($"Class split into {files.Count + 1} partial classes, numbered from {startNumber}.");
    }

    private static void ModifyOriginalClassToPartial(
        string filePath, 
        ClassDeclarationSyntax classDeclaration, 
        CompilationUnitSyntax root,
        NamespaceDeclarationSyntax? namespaceDeclaration, 
        FileScopedNamespaceDeclarationSyntax? fileScopedNamespace,
        List<MemberDeclarationSyntax> membersToKeep)
    {
        // Check if the class already has the partial modifier
        bool alreadyPartial = classDeclaration.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword));
        
        ClassDeclarationSyntax partialClassDeclaration;
        
        if (alreadyPartial)
        {
            // Just update the members while preserving trivia
            SyntaxList<MemberDeclarationSyntax> newMembers = SyntaxFactory.List(membersToKeep);
            partialClassDeclaration = classDeclaration.WithMembers(newMembers);
        }
        else
        {
            // Create a new partial token with leading and trailing trivia to ensure proper spacing
            SyntaxToken partialToken = SyntaxFactory.Token(
                SyntaxFactory.TriviaList(),
                SyntaxKind.PartialKeyword,
                SyntaxFactory.TriviaList(SyntaxFactory.Space));
                
            // Add partial modifier to the class and update members
            SyntaxTokenList newModifiers = SyntaxFactory.TokenList(
                classDeclaration.Modifiers
                    .Where(m => !m.IsKind(SyntaxKind.PartialKeyword))
                    .Concat([partialToken]));
                    
            SyntaxList<MemberDeclarationSyntax> newMembers = SyntaxFactory.List(membersToKeep);
            
            partialClassDeclaration = classDeclaration
                .WithModifiers(newModifiers)
                .WithMembers(newMembers);
        }
                
        if (membersToKeep.Count > 0)
        {
            MemberDeclarationSyntax firstMember = membersToKeep[0];
            MemberDeclarationSyntax updatedFirstMember = firstMember.WithLeadingTrivia(SyntaxFactory.TriviaList(SyntaxFactory.Whitespace("    ")));

            MemberDeclarationSyntax lastMember = membersToKeep[^1];
            MemberDeclarationSyntax updatedLastMember = lastMember.WithTrailingTrivia(SyntaxFactory.TriviaList());

            List<MemberDeclarationSyntax> updatedMembers = new List<MemberDeclarationSyntax>(membersToKeep)
            {
                [0] = updatedFirstMember
            };
            
            if (membersToKeep.Count == 1)
            {
                updatedMembers[0] = updatedFirstMember.WithTrailingTrivia(SyntaxFactory.TriviaList());
            }
            else
            {
                updatedMembers[membersToKeep.Count - 1] = updatedLastMember;
            }

            SyntaxList<MemberDeclarationSyntax> newMembers = SyntaxFactory.List(updatedMembers);
            partialClassDeclaration = partialClassDeclaration
                .WithMembers(newMembers)
                .WithCloseBraceToken(SyntaxFactory.Token(
                    SyntaxFactory.TriviaList(SyntaxFactory.LineFeed),
                    SyntaxKind.CloseBraceToken,
                    SyntaxFactory.TriviaList()));
        }
        
        CompilationUnitSyntax newRoot;
        
        if (namespaceDeclaration != null)
        {
            // Replace the class in the namespace
            NamespaceDeclarationSyntax newNamespace = namespaceDeclaration.ReplaceNode(
                classDeclaration, partialClassDeclaration);
                
            newRoot = root.ReplaceNode(namespaceDeclaration, newNamespace);
        }
        else if (fileScopedNamespace != null)
        {
            // Replace the class in the file-scoped namespace
            FileScopedNamespaceDeclarationSyntax newFileScopedNamespace = fileScopedNamespace.ReplaceNode(
                classDeclaration, partialClassDeclaration);
                
            newRoot = root.ReplaceNode(fileScopedNamespace, newFileScopedNamespace);
        }
        else
        {
            // Replace the class directly in the root
            newRoot = root.ReplaceNode(classDeclaration, partialClassDeclaration);
        }
        
        string originalContent = newRoot.ToFullString().Trim();
        File.WriteAllText(filePath, originalContent);
        
        // Count actual lines in the modified file
        int lineCount = originalContent.Split('\n').Length;
        Console.WriteLine($"Original file modified to be partial with {membersToKeep.Count} members and {lineCount} lines of code.");
    }

    private static int FindNextAvailableNumber(string directory, string fileName, string extension)
    {
        Regex regex = new Regex($"^{Regex.Escape(fileName)}(\\d+){Regex.Escape(extension)}$");
        List<string?> existingFiles = Directory.GetFiles(directory)
            .Select(Path.GetFileName)
            .Where(name => regex.IsMatch(name))
            .ToList();
        
        if (existingFiles.Count is 0)
        {
            return 2;
        }
        
        int highestNumber = existingFiles
            .Select(name => {
                Match match = regex.Match(name);
                return int.Parse(match.Groups[1].Value);
            })
            .Max();

        return highestNumber + 1;
    }
}
