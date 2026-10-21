namespace DarkCipher.AgentControl.Application.Setup;

/// <summary>Creates a small, disposable sample project for end-to-end tests — always a NEW folder under the app's
/// test-projects directory, never inside a user project.</summary>
public static class TestProjectFactory
{
    public static string Create(string testProjectsDirectory)
    {
        var dir = Path.Combine(testProjectsDirectory, "sample-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "README.md"),
            "# Sample project (DarkCipher AgentControl)\n\nA disposable project for testing your local coding stack.\n" +
            "Try asking the agent: \"Add a function that reverses a string to utils.py and a test for it.\"\n");
        File.WriteAllText(Path.Combine(dir, "utils.py"), "def add(a, b):\n    \"\"\"Return the sum of a and b.\"\"\"\n    return a + b\n");
        File.WriteAllText(Path.Combine(dir, "test_utils.py"), "from utils import add\n\n\ndef test_add():\n    assert add(2, 3) == 5\n");
        return dir;
    }
}
