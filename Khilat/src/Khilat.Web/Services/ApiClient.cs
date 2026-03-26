namespace Khilat.Web.Services;

using System.Net.Http.Headers;
using System.Net.Http.Json;
using Blazored.LocalStorage;

public class ApiClient
{
    private readonly HttpClient _http;
    private readonly ILocalStorageService _localStorage;

    public ApiClient(HttpClient http, ILocalStorageService localStorage)
    {
        _http = http;
        _localStorage = localStorage;
    }

    private async Task SetAuthHeaderAsync()
    {
        var token = await _localStorage.GetItemAsStringAsync("authToken");
        if (!string.IsNullOrEmpty(token))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim('"'));
    }

    public async Task<T?> GetAsync<T>(string url)
    {
        await SetAuthHeaderAsync();
        return await _http.GetFromJsonAsync<T>(url);
    }

    public async Task<TResponse?> PostAsync<TRequest, TResponse>(string url, TRequest data)
    {
        await SetAuthHeaderAsync();
        var response = await _http.PostAsJsonAsync(url, data);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TResponse>();
    }

    public async Task<TResponse?> PutAsync<TRequest, TResponse>(string url, TRequest data)
    {
        await SetAuthHeaderAsync();
        var response = await _http.PutAsJsonAsync(url, data);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TResponse>();
    }

    public async Task DeleteAsync(string url)
    {
        await SetAuthHeaderAsync();
        var response = await _http.DeleteAsync(url);
        response.EnsureSuccessStatusCode();
    }
}
