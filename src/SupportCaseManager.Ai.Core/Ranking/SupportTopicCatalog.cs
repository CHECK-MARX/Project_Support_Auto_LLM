namespace SupportCaseManager.Ai.Core.Ranking;

public static class SupportTopicCatalog
{
    public static TopicEntityCatalog Create(string? productName = null)
    {
        var products = new List<TopicAliasDefinition>();
        if (!string.IsNullOrWhiteSpace(productName))
        {
            products.Add(new TopicAliasDefinition { CanonicalName = productName.Trim() });
        }
        products.Add(new TopicAliasDefinition { CanonicalName = "Validate", Aliases = ["Perforce Validate"] });
        return new TopicEntityCatalog
        {
            Products = products,
            Components =
            [
                new TopicAliasDefinition { CanonicalName = "Validate", Aliases = ["Perforce Validate"] },
            ],
            Features =
            [
                new TopicAliasDefinition { CanonicalName = "Stream", Aliases = ["ストリーム"] },
                new TopicAliasDefinition { CanonicalName = "License", Aliases = ["ライセンス"] },
                new TopicAliasDefinition { CanonicalName = "IDE Plugin", Aliases = ["IDEプラグイン", "Eclipse Plugin"] },
                new TopicAliasDefinition { CanonicalName = "Build upload", Aliases = ["validate build", "build upload", "解析結果をアップロード"] },
            ],
            Objects =
            [
                new TopicAliasDefinition { CanonicalName = "Analysis result", Aliases = ["解析結果"] },
            ],
            Entities =
            [
                new TopicEntityAliasDefinition
                {
                    Kind = TopicEntityKind.Command,
                    CanonicalValue = "qacli validate build",
                    Aliases = ["validate build"],
                },
            ],
        };
    }
}
