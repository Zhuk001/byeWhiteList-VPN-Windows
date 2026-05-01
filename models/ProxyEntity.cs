namespace ByeWhiteList.Windows.Models
{
    public class ProxyEntity
    {
        public long Id { get; set; }
        public long GroupId { get; set; }
        public int Type { get; set; }
        public long UserOrder { get; set; }
        public long Tx { get; set; }
        public long Rx { get; set; }
        public long SpeedKbps { get; set; }
        public long SpeedTestBytes { get; set; }
        public int SpeedTestStatus { get; set; } // 0=unknown, 1=ok, 2=timeout, 3=error
        public string? SpeedTestError { get; set; }
        public int Status { get; set; }
        public int Ping { get; set; }
        public string? Error { get; set; }
        public string? BeanJson { get; set; }
        public string? Name { get; set; }

        public string DisplayNameText => DisplayName();

        public string DisplayName()
        {
            // Если есть сохранённое имя — показываем его
            if (!string.IsNullOrEmpty(Name))
                return Name;

            // Если нет — пробуем вытащить из BeanJson
            try
            {
                if (!string.IsNullOrEmpty(BeanJson))
                {
                    dynamic? obj = Newtonsoft.Json.JsonConvert.DeserializeObject(BeanJson);
                    if (obj != null)
                    {
                        string? name = obj.name;
                        if (!string.IsNullOrEmpty(name)) return name;

                        string? server = obj.server;
                        int? port = obj.port;
                        if (server != null) return $"{server}:{port}";
                    }
                }
            }
            catch { }

            return $"Сервер {Id}";
        }

    }

}
