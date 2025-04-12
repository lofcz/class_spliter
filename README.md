# Class Splitter

A tiny Roslyn-based utility for splitting long C# classes into shorter ones. Pass a path to the file/files/directory with classes and optionally the LOC limit, which should not be exceeded (defaults to 1 500). The input files will then be split (if needed) into multiple partial classes named `FileName2.cs`, `FileName3.cs`, etc. If the file `FileName2.cs` already exists, we check for the first free index and number starting from it.

```
Usage:
  Splitter [options]

Options:
  -f, --file <path>             A single C# file to split.
  -l, --files <path1 path2...>  A list of specific C# files to split.
  -d, --directory <dir_path>    A directory containing C# files to split.
  -r, --recursive               Process files in subdirectories when using --directory. [default: False]
  -m, --max-lines <count>       Maximum number of lines allowed per generated file. [default: 1500]
  --version                     Show version information
  -?, -h, --help                Show help and usage information
```
