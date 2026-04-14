using System;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;
using WebView2AppHost.SystemAgent;

namespace HostTests
{
    public class SystemAgentTests : IDisposable
    {
        private readonly string _workDir;

        public SystemAgentTests()
        {
            _workDir = Path.Combine(Path.GetTempPath(), "webview2-app-host-systemagent-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_workDir);
            FileSystem.SetWorkspace(_workDir);
        }

        public void Dispose()
        {
            try { FileSystem.SetWorkspace(Path.GetTempPath()); } catch { }
            try { Directory.Delete(_workDir, recursive: true); } catch { }
        }

        [Fact]
        public void GetWorkspace_AfterSetWorkspace_ReturnsSetPath()
        {
            var original = FileSystem.GetWorkspace();
            FileSystem.SetWorkspace(_workDir);
            var result = FileSystem.GetWorkspace();
            Assert.True(result.StartsWith(_workDir, StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void SetWorkspace_WithInvalidPath_ReturnsOriginalWorkspace()
        {
            var original = FileSystem.GetWorkspace();
            var result = FileSystem.SetWorkspace("nonexistent_path_12345");
            Assert.Equal(original, result);
        }

        [Fact]
        public void ListFiles_WithValidDirectory_ReturnsJsonArray()
        {
            File.WriteAllText(Path.Combine(_workDir, "test.txt"), "hello");
            Directory.CreateDirectory(Path.Combine(_workDir, "subdir"));

            var result = FileSystem.ListFiles(".");
            
            Assert.Contains("test.txt", result);
            Assert.Contains("subdir", result);
        }

        [Fact]
        public void ListFiles_WithNonExistentDirectory_ReturnsErrorJson()
        {
            var result = FileSystem.ListFiles("nonexistent_dir_12345");
            
            Assert.Contains("error", result, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ReadFile_WithValidFile_ReturnsContent()
        {
            var filePath = Path.Combine(_workDir, "readtest.txt");
            var content = "Hello, World! テスト";
            File.WriteAllText(filePath, content, Encoding.UTF8);

            var result = FileSystem.ReadFile("readtest.txt");
            
            Assert.Equal(content, result);
        }

        [Fact]
        public void ReadFile_OutsideWorkspace_ThrowsUnauthorizedAccessException()
        {
            var originalWorkspace = FileSystem.GetWorkspace();
            FileSystem.SetWorkspace(_workDir);
            
            try
            {
                var ex = Assert.Throws<UnauthorizedAccessException>(() => FileSystem.ReadFile("..\\..\\windows\\system.ini"));
                Assert.Contains("Access denied", ex.Message);
            }
            finally
            {
                FileSystem.SetWorkspace(originalWorkspace);
            }
        }

        [Fact]
        public void ReadFileLines_WithValidFile_ReturnsSpecificLines()
        {
            var filePath = Path.Combine(_workDir, "lines.txt");
            File.WriteAllText(filePath, "line1\nline2\nline3\nline4\nline5", Encoding.UTF8);

            var result = FileSystem.ReadFileLines("lines.txt", 2, 4);
            
            // Should return \n joined lines regardless of original format
            Assert.Equal("line2\nline3\nline4", result);
        }

        [Fact]
        public void ReplaceInFile_HandlesNewlinesFlexibly()
        {
            var filePath = Path.Combine(_workDir, "newlines.txt");
            // File has CRLF
            File.WriteAllText(filePath, "Line1\r\nLine2\r\nLine3", Encoding.UTF8);

            // Agent sends LF
            var result = FileSystem.ReplaceInFile("newlines.txt", "Line1\nLine2", "Modified");
            
            Assert.Contains("success", result);
            Assert.Equal("Modified\r\nLine3", File.ReadAllText(filePath));
        }

        [Fact]
        public void ReplaceInFile_EscapesDollarSign()
        {
            var filePath = Path.Combine(_workDir, "dollar.txt");
            File.WriteAllText(filePath, "Replace me", Encoding.UTF8);

            // $ in new_text should not be treated as regex variable
            var result = FileSystem.ReplaceInFile("dollar.txt", "Replace me", "$100");
            
            Assert.Contains("success", result);
            Assert.Equal("$100", File.ReadAllText(filePath));
        }

        [Fact]
        public void ReplaceInFile_OnlyReplacesFirstOccurrence()
        {
            var filePath = Path.Combine(_workDir, "duplicate.txt");
            File.WriteAllText(filePath, "Duplicate\nDuplicate", Encoding.UTF8);

            var result = FileSystem.ReplaceInFile("duplicate.txt", "Duplicate", "Unique");
            
            Assert.Contains("success", result);
            // Only the first one should be replaced
            Assert.Equal("Unique\nDuplicate", File.ReadAllText(filePath).Replace("\r\n", "\n"));
        }

        [Fact]
        public void ReadFileLines_WithInvalidFile_ReturnsErrorMessage()
        {
            var result = FileSystem.ReadFileLines("nonexistent.txt", 1, 10);
            
            Assert.StartsWith("Error:", result);
        }

        [Fact]
        public void WriteFile_CreatesFileSuccessfully()
        {
            var result = FileSystem.WriteFile("newfile.txt", "test content");
            
            Assert.Equal("File written successfully.", result);
            Assert.True(File.Exists(Path.Combine(_workDir, "newfile.txt")));
        }

        [Fact]
        public void WriteFile_CreatesDirectoryIfNotExists()
        {
            var result = FileSystem.WriteFile("subdir\\newfile.txt", "test content");
            
            Assert.Equal("File written successfully.", result);
            Assert.True(File.Exists(Path.Combine(_workDir, "subdir", "newfile.txt")));
        }

        [Fact]
        public void ReplaceInFile_WithExistingText_ReplacesSuccessfully()
        {
            var filePath = Path.Combine(_workDir, "replace.txt");
            File.WriteAllText(filePath, "Hello World", Encoding.UTF8);

            var result = FileSystem.ReplaceInFile("replace.txt", "World", "Universe");
            
            Assert.Contains("success", result, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Hello Universe", File.ReadAllText(filePath));
        }

        [Fact]
        public void ReplaceInFile_WithNonExistingText_ReturnsError()
        {
            var filePath = Path.Combine(_workDir, "replace2.txt");
            File.WriteAllText(filePath, "Hello World", Encoding.UTF8);

            var result = FileSystem.ReplaceInFile("replace2.txt", "Nonexistent", "Text");
            
            Assert.Contains("error", result, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("not found", result, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ListDirectoryTree_WithValidDirectory_ReturnsTreeString()
        {
            Directory.CreateDirectory(Path.Combine(_workDir, "dir1", "subdir1"));
            File.WriteAllText(Path.Combine(_workDir, "dir1", "file1.txt"), "content");

            var result = FileSystem.ListDirectoryTree(".", 2);
            
            Assert.Contains("dir1/", result);
            Assert.Contains("file1.txt", result);
        }

        [Fact]
        public void ListDirectoryTree_WithNonExistentDirectory_ReturnsError()
        {
            var result = FileSystem.ListDirectoryTree("nonexistent_dir", 2);
            
            Assert.True(result.Contains("Error:") || result.Contains("nonexistent_dir"), 
                $"Expected error or directory name, got: {result}");
        }

        [Fact]
        public void TerminalExecute_WithValidCommand_ReturnsJsonWithStdout()
        {
            var result = Terminal.Execute("echo hello");
            
            Assert.Contains("hello", result);
            Assert.Contains("stdout", result);
        }

        [Fact]
        public void TerminalExecute_WithFailingCommand_ReturnsNonZeroExitCode()
        {
            var result = Terminal.Execute("exit 1");
            
            Assert.Contains("\"code\":1", result);
            Assert.Contains("\"ok\":false", result);
        }

        [Fact]
        public void TerminalExecute_WithInvalidCommand_ReturnsError()
        {
            var result = Terminal.Execute("invalid_command_xyz_12345");
            
            Assert.NotNull(result);
            Assert.Contains("stderr", result);
        }

        [Fact]
        public void FileSystem_Security_RelativePathTraversalBlocked()
        {
            FileSystem.SetWorkspace(_workDir);
            
            var ex = Assert.Throws<UnauthorizedAccessException>(() => FileSystem.ReadFile("..\\..\\test.txt"));
            Assert.Contains("outside the permitted workspace", ex.Message);
        }

        [Fact]
        public void FileSystem_Security_AbsolutePathOutsideWorkspaceBlocked()
        {
            FileSystem.SetWorkspace(_workDir);
            
            var absolutePath = Path.GetFullPath(Path.Combine(_workDir, "..", "outside.txt"));
            var ex = Assert.Throws<UnauthorizedAccessException>(() => FileSystem.ReadFile(absolutePath));
            Assert.Contains("Access denied", ex.Message);
        }

        [Fact]
        public void WriteFile_OverwritesExistingFile()
        {
            var filePath = Path.Combine(_workDir, "overwrite.txt");
            File.WriteAllText(filePath, "original");
            FileSystem.WriteFile("overwrite.txt", "updated");
            
            Assert.Equal("updated", File.ReadAllText(filePath));
        }

        [Fact]
        public void ReadFile_EmptyFile_ReturnsEmptyString()
        {
            var filePath = Path.Combine(_workDir, "empty.txt");
            File.WriteAllText(filePath, "", Encoding.UTF8);

            var result = FileSystem.ReadFile("empty.txt");
            
            Assert.Equal("", result);
        }

        [Fact]
        public void ListFiles_EmptyDirectory_ReturnsEmptyArrayJson()
        {
            var emptyDir = Path.Combine(_workDir, "empty");
            Directory.CreateDirectory(emptyDir);
            FileSystem.SetWorkspace(_workDir);

            var result = FileSystem.ListFiles("empty");
            
            Assert.Equal("[]", result);
        }
    }
}