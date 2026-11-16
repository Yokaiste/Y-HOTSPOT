namespace HotspotApp.Logging;
public static class ManualTestDiagnostics { public static void Write(string text, Exception? error = null) { } }
public static class LogPaths { public static string DirectoryPath => throw new Exception("Testes não podem acessar disco de produção"); }
