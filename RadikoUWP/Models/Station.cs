namespace RadikoUWP.Models
{
    public class Station
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string AreaId { get; set; }

        public override string ToString()
        {
            return Name;
        }
    }
}
