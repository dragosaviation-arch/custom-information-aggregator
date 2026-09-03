namespace CIA.ProcessingHost.Tests;

[TestClass]
public sealed class TestDiscovery
{
    [TestMethod]
    public void ProcessingHostAssemblyUsesTheInternalExecutableName()
    {
        Assert.AreEqual(
            "CIA.ProcessingHost",
            typeof(global::CIA.ProcessingHost.Program).Assembly.GetName().Name);
    }
}
