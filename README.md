# Class Splitter

This is a tiny program for splitting long classes into shorter ones. Pass a path to the file, and optionally the file length (in lines of code) which should not be exceeded. The file will be then split (if needed) into multiple partial classes named `FileName2.cs`, `FileName3.cs`, etc. If the file `FileName2.cs` already exists, we check for the first free index and number from that.

Usage: `ClassSplitter.exe <filepath> [maxlines=1500]`
