public class MockHttpHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses;
    public int CallCount { get; private set; }
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }

    public MockHttpHandler(HttpResponseMessage response)
    {
        _responses = new Queue<HttpResponseMessage>([response]);
    }

    public MockHttpHandler(HttpResponseMessage[] responses)
    {
        _responses = new Queue<HttpResponseMessage>(responses);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        CallCount++;
        LastRequest = request;

        if (request.Content is not null)
            LastRequestBody = await request.Content.ReadAsStringAsync(ct);

        var response = _responses.Count > 1 ? _responses.Dequeue() : _responses.Peek();
        return response;
    }
}