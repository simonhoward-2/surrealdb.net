using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Bogus.DataSets;
using Dahomey.Cbor.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using SurrealDb.Examples.WeatherApi.Models;

namespace SurrealDb.Net.Tests;

public class WeatherForecastDto
{
    public string? Id { get; set; }

    public DateTime Date { get; set; }

    public string? Country { get; set; }

    public int TemperatureC { get; set; }

    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);

    public string? Summary { get; set; }
}

public class ControllerIntegrationTests
{
    private const int ControllerExperimentRepeatCount = 5;
    private const int MimicExperimentRepeatCount = 10;

    /// <summary>
    /// Integration test for the WeatherForecast Example API.
    /// This test demonstrates that the API cannot handle concurrent requests, as the internal
    /// SurrealDB Engine encounters a timeout when multiple requests are made in parallel.
    /// Note:
    ///     - The WeatherForecastExample is using a ws:// connection.
    ///     - The SurrealDB Server must be running in RocksDB mode.
    /// </summary>
    [Test]
    public async Task ControllerIntegrationTest()
    {
        // Generally this won't happen consistently, but repeating the experiment multiple times
        // (sequentially) should yield the fail result.
        for (int i = 0; i < ControllerExperimentRepeatCount; i++)
        {
            Console.WriteLine($"Running test iteration {i + 1} at {DateTime.Now:HH:mm:ss.fff}");
            await ControllerIntegrationTestSingle();
            Console.WriteLine($"Finished test iteration {i + 1} at {DateTime.Now:HH:mm:ss.fff}");
        }
    }

    private async Task ControllerIntegrationTestSingle()
    {
        // Create a WebApplicationFactory for the API
        var factory = new WebApplicationFactory<Program>();

        // Create an HttpClient to send requests to the API
        using var httpClient = factory.CreateClient();

        var stopwatchGetAll = new Stopwatch();
        stopwatchGetAll.Start();

        // Get all the data from the WeatherForecast endpoint
        var response = await httpClient.GetAsync($"WeatherForecast");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var forecasts = JsonConvert.DeserializeObject<List<WeatherForecastDto>>(content);

        stopwatchGetAll.Stop();
        Console.WriteLine(
            "Time to get all weather forecasts "
                + stopwatchGetAll.Elapsed.TotalMilliseconds.ToString("#.##")
                + " miliseconds"
        );

        var stopwatchConcurrentGets = new Stopwatch();
        stopwatchConcurrentGets.Start();

        // Ddjust this to the concurrent calls. Only 2 is required.
        const int concurrentCallCount = 2;
        var tasks = new Task[concurrentCallCount];
        for (int i = 0; i < concurrentCallCount; i++)
        {
            tasks[i] = GetWeatherForecastAndTimeIt(httpClient, forecasts?.First()!);
        }
        await Task.WhenAll(tasks);

        stopwatchConcurrentGets.Stop();
        Console.WriteLine(
            "Time to do all tasks "
                + stopwatchConcurrentGets.Elapsed.TotalMilliseconds.ToString("#.##")
                + " miliseconds"
        );

        // If response takes longer than 20s, we can suspect there has been a timeout in the internals
        stopwatchConcurrentGets
            .Elapsed.TotalSeconds.Should()
            .BeLessThan(20, "Concurrent gets should not take more than 20 seconds");
    }

    private async Task GetWeatherForecastAndTimeIt(
        HttpClient httpClient,
        WeatherForecastDto forecast
    )
    {
        Console.WriteLine($"Running Get for weather forecast. {DateTime.Now:HH:mm:ss.fff}");
        var stopwatchGet = new Stopwatch();
        stopwatchGet.Start();

        var response = await httpClient.GetAsync($"WeatherForecast/{forecast.Id}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var selectedForecast = JsonConvert.DeserializeObject<WeatherForecastDto>(
            await response.Content.ReadAsStringAsync()
        );
        stopwatchGet.Stop();
        Console.WriteLine(
            $"Result: {selectedForecast?.Id}. Time to select {stopwatchGet.Elapsed.TotalMilliseconds.ToString("#.##")} miliseconds. Time: {DateTime.Now.ToString("HH:mm:ss.fff")}"
        );
    }

    /// <summary>
    /// Test designed to replicate thread handling scenarios used in the .Net BaseController class.
    /// This test is slightly less reliable than the controller integration test, but will still
    /// yield a timeout in the SurrealDB Engine.
    /// </summary>
    [Test]
    [ConnectionStringFixtureGenerator]
    public async Task ControllerThreadingMimicTest(string connectionString)
    {
        // Generally this won't happen consistently, but repeating the experiment multiple times
        // (sequentially) should yield the fail result.
        for (int i = 0; i < MimicExperimentRepeatCount; i++)
        {
            Console.WriteLine($"Running test iteration {i + 1} at {DateTime.Now:HH:mm:ss.fff}");
            await ControllerThreadingMimicTestSingle(connectionString);
            Console.WriteLine($"Finished test iteration {i + 1} at {DateTime.Now:HH:mm:ss.fff}");
        }
    }

    private async Task ControllerThreadingMimicTestSingle(string connectionString)
    {
        SurrealDbClient? client = null;

        var stopwatchInitialise = new Stopwatch();
        stopwatchInitialise.Start();

        Func<Task> clientCreatefunc = async () =>
        {
            var surrealDbClientGenerator = new SurrealDbClientGenerator();
            var dbInfo = surrealDbClientGenerator.GenerateDatabaseInfo();

            client = surrealDbClientGenerator.Create(connectionString);
            await client.Use(dbInfo.Namespace, dbInfo.Database);

            await client.ApplySchemaAsync(SurrealSchemaFile.Post);
        };

        await clientCreatefunc.Should().NotThrowAsync();

        stopwatchInitialise.Stop();
        Console.WriteLine(
            "Time to setup schema "
                + stopwatchInitialise.Elapsed.TotalMilliseconds.ToString("#.##")
                + " miliseconds"
        );

        // Perform single select
        var stopwatchSingleSelect = new Stopwatch();
        stopwatchSingleSelect.Start();
        Console.WriteLine($"Running single select method. {DateTime.Now:HH:mm:ss.fff}");
        await RunSelectMethod(client!);
        stopwatchSingleSelect.Stop();
        Console.WriteLine(
            $"Time to do single select {stopwatchSingleSelect.Elapsed.TotalMilliseconds.ToString("#.##")} miliseconds"
        );

        var stopWatchConcurrentSelects = new Stopwatch();
        stopWatchConcurrentSelects.Start();

        // Adjust this to the number of parallel tasks you want to run, only 2 is required
        const int concurrentTaskCount = 2;
        var tasks = new Task[concurrentTaskCount];
        for (int i = 0; i < concurrentTaskCount; i++)
        {
            tasks[i] = Task.Run(() => RunSelectMethod(client!));
        }
        await Task.WhenAll(tasks);

        stopWatchConcurrentSelects.Stop();
        Console.WriteLine(
            "Time to do all tasks "
                + stopWatchConcurrentSelects.Elapsed.TotalMilliseconds.ToString("#.##")
                + " miliseconds"
        );

        // If response takes longer than 20s, we can suspect there has been a timeout in the internals
        stopWatchConcurrentSelects
            .Elapsed.TotalSeconds.Should()
            .BeLessThan(20, "Concurrent selects should not take more than 20 seconds");
    }

    private async Task RunSelectMethod(SurrealDbClient surrealDbClient)
    {
        var stopwatchSelect = new Stopwatch();
        stopwatchSelect.Start();

        Console.WriteLine($"Running select method. {DateTime.Now:HH:mm:ss.fff}");
        var record = await surrealDbClient.Select<Post>(new RecordIdOf<string>("post", "first"));

        stopwatchSelect.Stop();
        Console.WriteLine(
            $"Result: {record?.Id?.DeserializeId<string>()} Time to select {stopwatchSelect.Elapsed.TotalMilliseconds.ToString("#.##")} miliseconds. Time: {DateTime.Now.ToString("HH:mm:ss.fff")}"
        );
    }
}
