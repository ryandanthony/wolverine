using Amazon.SQS;
using Amazon.SQS.Model;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.AmazonSqs.Internal;

public class AmazonSqsQueue : Endpoint, IAmazonSqsListeningEndpoint, IBrokerQueue
{
    private readonly AmazonSqsTransport _parent;
    
    // This will vary later
    private ISqsEnvelopeMapper _mapper = new DefaultSqsEnvelopeMapper();

    private bool _initialized;
    
    internal AmazonSqsQueue(string queueName, AmazonSqsTransport parent) : base(new Uri($"sqs://{queueName}"),
        EndpointRole.Application)
    {
        _parent = parent;
        QueueName = queueName;
        EndpointName = queueName;

        Configuration = new CreateQueueRequest(QueueName);
    }

    /// <summary>
    /// Pluggable strategy for interoperability with non-Wolverine systems. Customizes how the incoming SQS requests
    /// are read and how outgoing messages are written to SQS
    /// </summary>
    /// <exception cref="ArgumentNullException"></exception>
    public ISqsEnvelopeMapper Mapper
    {
        get => _mapper;
        set => _mapper = value ?? throw new ArgumentNullException(nameof(value));
    }

    internal async Task SendMessageAsync(Envelope envelope, ILogger logger)
    {
        if (!_initialized)
        {
            await InitializeAsync(logger);
        }

        var body = _mapper.BuildMessageBody(envelope);
        var request = new SendMessageRequest(QueueUrl, body);
        foreach (var attribute in _mapper.ToAttributes(envelope))
        {
            request.MessageAttributes.Add(attribute.Key, attribute.Value);
        }

        await _parent.Client!.SendMessageAsync(request);
    }


    public string QueueName { get; }

    // Set by the AmazonSqsTransport parent
    internal string? QueueUrl { get; private set; }

    /// <summary>
    ///     The duration (in seconds) that the received messages are hidden from subsequent retrieve
    ///     requests after being retrieved by a <code>ReceiveMessage</code> request. The default is
    ///     120.
    /// </summary>
    public int VisibilityTimeout { get; set; } = 120;

    /// <summary>
    ///     The duration (in seconds) for which the call waits for a message to arrive in the
    ///     queue before returning. If a message is available, the call returns sooner than <code>WaitTimeSeconds</code>.
    ///     If no messages are available and the wait time expires, the call returns successfully
    ///     with an empty list of messages. Default is 5.
    /// </summary>
    public int WaitTimeSeconds { get; set; } = 5;

    /// <summary>
    ///     The maximum number of messages to return. Amazon SQS never returns more messages than
    ///     this value (however, fewer messages might be returned). Valid values: 1 to 10. Default:
    ///     10.
    /// </summary>
    public int MaxNumberOfMessages { get; set; } = 10;

    /// <summary>
    ///     Additional configuration for how an SQS queue should be created
    /// </summary>
    public CreateQueueRequest Configuration { get; }

    /// <summary>
    /// Name of the dead letter queue for this SQS queue where failed messages will be moved
    /// </summary>
    public string? DeadLetterQueueName { get; set; } = AmazonSqsTransport.DeadLetterQueueName;

    public async ValueTask<bool> CheckAsync()
    {
        var response = await _parent.Client!.GetQueueUrlAsync(QueueName);
        return response.QueueUrl.IsNotEmpty();
    }

    public async ValueTask TeardownAsync(ILogger logger)
    {
        var client = _parent.Client!;

        if (QueueUrl.IsEmpty())
        {
            var response = await client.GetQueueUrlAsync(QueueName);
            QueueUrl = response.QueueUrl;
        }

        if (QueueUrl.IsEmpty())
        {
            return;
        }

        await client.DeleteQueueAsync(new DeleteQueueRequest(QueueUrl));
    }

    public ValueTask SetupAsync(ILogger logger)
    {
        return new ValueTask(SetupAsync(_parent.Client!));
    }

    public ValueTask PurgeAsync(ILogger logger)
    {
        return new ValueTask(PurgeAsync(_parent.Client!));
    }

    public async ValueTask<Dictionary<string, string>> GetAttributesAsync()
    {
        var client = _parent.Client!;

        if (QueueUrl.IsEmpty())
        {
            var response = await client.GetQueueUrlAsync(QueueName);
            QueueUrl = response.QueueUrl;
        }

        var atts = await client.GetQueueAttributesAsync(new GetQueueAttributesRequest
        {
            QueueUrl = QueueUrl
        });

        return new Dictionary<string, string>
        {
            { "name", QueueName },
            {
                nameof(GetQueueAttributesResponse.ApproximateNumberOfMessages),
                atts.ApproximateNumberOfMessages.ToString()
            },
            {
                nameof(GetQueueAttributesResponse.ApproximateNumberOfMessagesDelayed),
                atts.ApproximateNumberOfMessagesDelayed.ToString()
            },
            {
                nameof(GetQueueAttributesResponse.ApproximateNumberOfMessagesNotVisible),
                atts.ApproximateNumberOfMessagesNotVisible.ToString()
            }
        };
    }

    public override async ValueTask InitializeAsync(ILogger logger)
    {
        if (_initialized)
        {
            return;
        }

        var client = _parent.Client;

        if (client == null)
        {
            throw new InvalidOperationException($"Parent {nameof(AmazonSqsTransport)} has not been initialized");
        }

        if (_parent.AutoProvision)
        {
            await SetupAsync(client);
            logger.LogInformation("Tried to create Amazon SQS queue {Name} if missing", QueueUrl);
        }

        if (QueueUrl.IsEmpty())
        {
            var response = await client.GetQueueUrlAsync(QueueName);
            QueueUrl = response.QueueUrl;
        }

        if (_parent.AutoPurgeAllQueues)
        {
            await PurgeAsync(logger);
            logger.LogInformation("Purging Amazon SQS queue {Name}", QueueUrl);
        }

        _initialized = true;
    }

    internal async Task SetupAsync(IAmazonSQS client)
    {
        Configuration.QueueName = QueueName;
        var response = await client.CreateQueueAsync(Configuration);

        QueueUrl = response.QueueUrl;
    }

    public async Task PurgeAsync(IAmazonSQS client)
    {
        if (QueueUrl.IsEmpty())
        {
            var response = await client.GetQueueUrlAsync(QueueName);
            QueueUrl = response.QueueUrl;
        }

        try
        {
            await client.PurgeQueueAsync(QueueUrl);
        }
        catch (PurgeQueueInProgressException e)
        {
            Console.WriteLine(e.Message);
        }
    }

    public override async ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        if (_parent.Client == null)
        {
            throw new InvalidOperationException("The parent transport has not yet been initialized");
        }

        if (QueueUrl.IsEmpty())
        {
            await InitializeAsync(runtime.LoggerFactory.CreateLogger<AmazonSqsQueue>());
        }

        return new SqsListener(runtime, this, _parent, receiver);
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        if (Mode == EndpointMode.Inline) return new InlineSqsSender(runtime, this);
        
        var protocol = new SqsSenderProtocol(runtime, this,
            _parent.Client ?? throw new InvalidOperationException("Parent transport has not been initialized"));
        return new BatchedSender(Uri, protocol, runtime.Cancellation,
            runtime.LoggerFactory.CreateLogger<SqsSenderProtocol>());
    }

    protected override bool supportsMode(EndpointMode mode)
    {
        return true;
    }

    internal void ConfigureRequest(ReceiveMessageRequest request)
    {
        request.WaitTimeSeconds = WaitTimeSeconds;
        request.MaxNumberOfMessages = MaxNumberOfMessages;
        request.VisibilityTimeout = VisibilityTimeout;
    }


    public async Task TeardownAsync(IAmazonSQS client, CancellationToken token)
    {
        if (QueueUrl == null)
        {
            try
            {
                QueueUrl = (await client.GetQueueUrlAsync(QueueName, token)).QueueUrl;
            }
            catch (Exception)
            {
                return;
            }
        }

        await client.DeleteQueueAsync(new DeleteQueueRequest
        {
            QueueUrl = QueueUrl
        }, token);
    }

    internal void ConfigureDeadLetterQueue(Action<AmazonSqsQueue> configure)
    {
        var dlq = _parent.Queues[DeadLetterQueueName];
        configure(dlq);
    }

    public override bool TryBuildDeadLetterSender(IWolverineRuntime runtime, out ISender? deadLetterSender)
    {
        if (DeadLetterQueueName.IsNotEmpty())
        {
            var dlq = _parent.Queues[DeadLetterQueueName];
            deadLetterSender = new InlineSqsSender(runtime, dlq);
            return true;
        }

        deadLetterSender = default;
        return false;
    }
}