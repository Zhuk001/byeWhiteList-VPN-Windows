namespace ByeWhiteList.Windows.Models
{
    public class ProxyGroup
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public int Type { get; set; }
        public long UserOrder { get; set; }
        public string? SubscriptionUrl { get; set; }
    }
}
