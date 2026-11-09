namespace SupportCaseManager.App.Outlook;

internal static class OutlookManufacturerSupport
{
    // Exact published support mailboxes, scoped to the product. Do not infer a
    // party from arbitrary addresses on the vendor's domain or from mail body text.
    // https://checkmarx.com/wp-content/uploads/2025/01/How-to-Work-With-Checkmarx-Support.pdf
    // https://www.perforce.com/system/files/2025-02/Klocwork%20Evaluation-License%20Instructions.pdf
    internal static bool IsPublishedSupportAddress(string product, string address) =>
        product.Equals("Checkmarx", StringComparison.OrdinalIgnoreCase)
            && address.Equals("support@checkmarx.com", StringComparison.OrdinalIgnoreCase)
        || (product.Equals("Klocwork", StringComparison.OrdinalIgnoreCase) || product.Equals("Klcwork", StringComparison.OrdinalIgnoreCase))
            && address.Equals("support-klocwork@perforce.com", StringComparison.OrdinalIgnoreCase);
}
