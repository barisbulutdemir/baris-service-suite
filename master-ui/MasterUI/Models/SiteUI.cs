namespace MasterUI.Models
{
    public class SiteUI
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Status { get; set; } = "";
        public string RustDeskId { get; set; } = "";
        public string RustDeskPassword { get; set; } = "";
        public bool IsOnline => Status == "online";
    }
}
