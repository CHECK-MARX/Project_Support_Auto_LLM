namespace SupportCaseManager.Ai.Core.Ranking;

public static class SupportTopicCatalog
{
    public static TopicEntityCatalog Create(string? productName = null)
    {
        var products = new List<TopicAliasDefinition>();
        if (!string.IsNullOrWhiteSpace(productName))
        {
            var isCheckmarx = productName.Contains("checkmarx", StringComparison.OrdinalIgnoreCase) ||
                productName.Contains("cxsast", StringComparison.OrdinalIgnoreCase);
            products.Add(new TopicAliasDefinition
            {
                CanonicalName = productName.Trim(),
                Aliases = isCheckmarx ? ["Checkmarx SAST", "CxSAST", "SAST"] : [],
            });
        }
        products.Add(new TopicAliasDefinition { CanonicalName = "Validate", Aliases = ["Perforce Validate"] });
        return new TopicEntityCatalog
        {
            Products = products,
            Components =
            [
                new TopicAliasDefinition { CanonicalName = "Validate", Aliases = ["Perforce Validate"] },
                new TopicAliasDefinition { CanonicalName = "CxSAST", Aliases = ["Checkmarx SAST", "SAST"] },
            ],
            Features =
            [
                new TopicAliasDefinition { CanonicalName = "Stream", Aliases = ["ストリーム"] },
                new TopicAliasDefinition { CanonicalName = "License", Aliases = ["ライセンス"] },
                new TopicAliasDefinition { CanonicalName = "IDE Plugin", Aliases = ["IDEプラグイン", "Eclipse Plugin"] },
                new TopicAliasDefinition { CanonicalName = "CCT", Aliases = ["Compiler Compatibility Template", "CCT", "コンパイラ互換性テンプレート"] },
                new TopicAliasDefinition { CanonicalName = "Project Analysis", Aliases = ["project analysis", "プロジェクトの解析", "プロジェクトを解析", "解析CLI", "解析 CLI", "解析コマンド"] },
                new TopicAliasDefinition { CanonicalName = "Dashboard", Aliases = ["ダッシュボード"] },
                new TopicAliasDefinition { CanonicalName = "Backup", Aliases = ["バックアップ"] },
                new TopicAliasDefinition { CanonicalName = "Build upload", Aliases = ["validate build", "build upload", "解析結果をアップロード"] },
                new TopicAliasDefinition
                {
                    CanonicalName = "File delivery",
                    Aliases = ["Fiebie", "Fibe", "ファイル転送", "ダウンロードサイト", "ファイル提供", "代替提供", "インストーラ入手"],
                },
                new TopicAliasDefinition
                {
                    CanonicalName = "Supported Languages",
                    Aliases = ["対応言語", "解析対象", "supported languages", "language support"],
                },
                new TopicAliasDefinition
                {
                    CanonicalName = "Release Notes",
                    Aliases = [
                        "リリースノート", "リリース内容", "変更内容", "変更点", "追加機能", "新機能", "修正内容", "対応内容", "バージョン情報",
                        "release notes", "release note", "released", "enhancement", "resolved issues", "what's new", "engine pack",
                    ],
                },
            ],
            Objects =
            [
                new TopicAliasDefinition { CanonicalName = "Analysis result", Aliases = ["解析結果"] },
                new TopicAliasDefinition { CanonicalName = "Stored Procedure", Aliases = ["Stored Procedure", "Stored Procedures", "ストアド", "ストアドプロシージャ"] },
            ],
            Entities =
            [
                new TopicEntityAliasDefinition
                {
                    Kind = TopicEntityKind.Command,
                    CanonicalValue = "qacli validate build",
                    Aliases = ["validate build"],
                },
                new TopicEntityAliasDefinition
                {
                    Kind = TopicEntityKind.Api,
                    CanonicalValue = "Microsoft SQL Server",
                    Aliases = ["SQL Server", "MS SQL Server", "MSSQL"],
                },
                new TopicEntityAliasDefinition
                {
                    Kind = TopicEntityKind.Api,
                    CanonicalValue = "T-SQL",
                    Aliases = ["Transact-SQL", "Transact SQL"],
                },
                new TopicEntityAliasDefinition
                {
                    Kind = TopicEntityKind.Api,
                    CanonicalValue = "PL/SQL",
                    Aliases = ["PLSQL", "Oracle PL/SQL"],
                },
            ],
        };
    }
}
