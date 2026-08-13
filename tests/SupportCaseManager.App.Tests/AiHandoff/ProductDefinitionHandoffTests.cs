using SupportCaseManager.App.AiHandoff;

namespace SupportCaseManager.App.Tests.AiHandoff;

public sealed class ProductDefinitionHandoffTests
{
    [Fact]
    public void BuildFromCurrentState_TransfersStableProductAndPromptSettings()
    {
        var id = Guid.NewGuid();
        var state = new AiAssistantCurrentState
        {
            ProductId = id,
            ProductName = " ConfigurableProduct ",
            ProductPromptFilePath = @" prompts\products\configurable.txt ",
            SupportToolSettingsFilePath = @" C:\Settings\user-settings.json ",
        };

        var context = new AiAssistantLaunchContextBuilder().BuildFromCurrentState(state);

        Assert.Equal(id, context.ProductId);
        Assert.Equal("ConfigurableProduct", context.ProductName);
        Assert.Equal(@"prompts\products\configurable.txt", context.ProductPromptFilePath);
        Assert.Equal(@"C:\Settings\user-settings.json", context.SupportToolSettingsFilePath);
    }
}
