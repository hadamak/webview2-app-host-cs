$files = @(
    "tests\HostTests\McpTests.cs",
    "tests\HostTests\SidecarTests.cs",
    "tests\HostTests\ConnectorQualityTests.cs",
    "tests\HostTests\SecureOfflineTests.cs"
)

foreach ($file in $files) {
    if (-not (Test-Path $file)) { continue }
    
    $content = Get-Content $file -Raw
    
    # 1. Add using Xunit;
    if ($content -notmatch "using Xunit;") {
        $content = $content -replace "using System;", "using System;`r`nusing Xunit;"
    }
    
    # 2. Add IDisposable and Constructor/Dispose for the class
    $className = ""
    if ($content -match "internal static class (\w+Tests)") {
        $className = $matches[1]
        $content = $content -replace "internal static class $className", "public class $className : IDisposable"
    }

    # 3. Replace RunAll method with Constructor and Dispose (handling AppLog.Override)
    $runAllPattern = "(?s)internal static void RunAll\(\).*?\{.*?(var old = AppLog.Override;.*?AppLog.Override = TextWriter.Null;).*?try.*?\{.*?\}.*?finally.*?\{.*?\}.*?\}"
    
    $constructorAndDispose = "private readonly TextWriter _oldLog;`r`n`r`n        public $className()`r`n        {`r`n            _oldLog = AppLog.Override;`r`n            AppLog.Override = TextWriter.Null;`r`n        }`r`n`r`n        public void Dispose()`r`n        {`r`n            AppLog.Override = _oldLog;`r`n        }"

    if ($content -match $runAllPattern) {
        $content = $content -replace $runAllPattern, $constructorAndDispose
    } else {
        # Fallback if RunAll is simpler (like SecureOfflineTests doesn't touch AppLog)
        $simpleRunAll = "(?s)internal static void RunAll\(\).*?\{.*?\}"
        $content = $content -replace $simpleRunAll, ""
        $content = $content -replace "public class $className : IDisposable", "public class $className"
    }
    
    # 4. Replace private static void RunXXXTests() with [Fact] public void XXXTests()
    $content = $content -replace "private static void Run(\w+Tests)\(\)", "[Fact]`r`n        public void `$1()"
    
    # 5. Replace Assert( -> Assert.True(
    # But only the method calls, not the definition. The definition is:
    # private static void Assert(bool cond, string label)
    $content = $content -replace "(?s)private static void Assert\(bool cond, string label\).*?\{.*?\}", ""
    $content = $content -replace "(?<!\.)\bAssert\(", "Assert.True("
	
	# If any static methods remain, make them non-static (except helpers if we want, but non-static is safer for parallelism)
	# Wait, helpers like RunServer can remain static or become instance. Let's just remove "static" from methods inside the class.
	# Actually, Xunit doesn't complain about private static helpers. Let's leave them.

    Set-Content -Path $file -Value $content -Encoding UTF8
}
