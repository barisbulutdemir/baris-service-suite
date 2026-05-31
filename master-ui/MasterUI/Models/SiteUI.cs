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

        // Geolocation properties
        public string Country { get; set; } = "";
        public string City { get; set; } = "";
        public double? Lat { get; set; }
        public double? Lon { get; set; }
        public string Isp { get; set; } = "";

        public bool HasLocation => !string.IsNullOrEmpty(Country) || !string.IsNullOrEmpty(City);
        public string LocationString => HasLocation ? $"{City}, {Country} ({Isp})" : "";
    }
}
