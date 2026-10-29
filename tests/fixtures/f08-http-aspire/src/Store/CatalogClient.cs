namespace Store;

public sealed class CatalogClient(HttpClient http)
{
    private readonly string _prefix = "api/items/";

    public Task<string> Get(int id) => http.GetStringAsync($"{_prefix}{id}");

    public Task<string> Many() => http.GetStringAsync("api/items/by?ids=1");

    public Task<string> Missing() => http.GetStringAsync("api/nothing");
}
