using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml.Linq;
using RadikoUWP.Models;

namespace RadikoUWP.Services
{
    public class RadikoStationService
    {
        private const string STATION_LIST_URL = "https://radiko.jp/v3/station/list/{0}.xml";

        private static readonly HttpClient _httpClient = new HttpClient();

        public async Task<List<Station>> GetStationsAsync(string areaId)
        {
            var stations = new List<Station>();

            var url = string.Format(STATION_LIST_URL, areaId);
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var xmlContent = await response.Content.ReadAsStringAsync();
            var doc = XDocument.Parse(xmlContent);

            foreach (var stationElement in doc.Descendants("station"))
            {
                var id = stationElement.Element("id")?.Value;
                var name = stationElement.Element("name")?.Value;

                if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name))
                {
                    stations.Add(new Station
                    {
                        Id = id,
                        Name = name,
                        AreaId = areaId
                    });
                }
            }

            return stations;
        }
    }
}

