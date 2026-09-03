namespace CIA.Desktop.Tests;

[TestClass]
public sealed class TestDiscovery
{
    [TestMethod]
    public void DesktopAssemblyUsesTheUserFacingExecutableName()
    {
        Assert.AreEqual("CIA", typeof(global::CIA.Desktop.App).Assembly.GetName().Name);
    }
}
