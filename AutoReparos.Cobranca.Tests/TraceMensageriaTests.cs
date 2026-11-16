using Amazon.SQS.Model;
using AutoReparos.Cobranca.Api;
using AutoReparos.Cobranca.Application;
using AutoReparos.Cobranca.Domain;
using AutoReparos.Cobranca.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace AutoReparos.Cobranca.Tests;

public sealed partial class BillingLifecycleTests
{
    // Propagação do contexto de trace W3C pelos envelopes (D-65): produtor grava, publicador e consumidor abrem spans ligados a ele.
    public sealed class TraceMensageriaTests : IDisposable
    {
        private const string FonteMensageria = "AutoReparos.Cobranca.Mensageria";
        private const string NomeFonteTeste = "AutoReparos.Cobranca.Tests.Trace";
        private static readonly ActivitySource FonteTeste = new(NomeFonteTeste);
        private readonly ConcurrentQueue<Activity> _spans = new();
        private readonly ActivityListener _listener;

        public TraceMensageriaTests()
        {
            Activity.Current = null;
            _listener = new ActivityListener
            {
                ShouldListenTo = fonte => fonte.Name == FonteMensageria || fonte.Name == NomeFonteTeste,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = _spans.Enqueue,
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public void Dispose() { Activity.Current = null; _listener.Dispose(); }

        private sealed class Processados : IEventosProcessadosStore
        {
            public readonly List<EventoProcessado> Eventos = [];
            public Task<bool> ExisteAsync(Guid eventId, CancellationToken ct) => Task.FromResult(Eventos.Any(e => e.Id == eventId));
            public void Registrar(EventoProcessado evento) => Eventos.Add(evento);
        }

        private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Sqs:QueueUrl"] = "main",
            ["Sqs:DeadLetterQueueUrl"] = "dlq",
            ["Sqs:ComandosQueueUrl"] = "cmd",
            ["Sqs:ComandosDeadLetterQueueUrl"] = "cmd-dlq",
        }).Build();

        private static Activity IniciarAtividadeCorrente(string? tracestate = null)
        {
            var atividade = FonteTeste.StartActivity("requisicao de teste")!;
            if (tracestate is not null) atividade.TraceStateString = tracestate;
            return atividade;
        }

        private Activity[] SpansDaMensageria(ActivityKind kind, Func<Activity, bool> filtro) =>
            [.. _spans.Where(s => s.Source.Name == FonteMensageria && s.Kind == kind && filtro(s))];

        private static string? Campo(string json, string nome)
        {
            using var documento = JsonDocument.Parse(json);
            return documento.RootElement.TryGetProperty(nome, out var valor) ? valor.GetString() : null;
        }

        private static async Task<(ComandosConsumer Consumer, FakeSqs Sqs, Store Store, Orcamento Orcamento)> MontarConsumidorAsync()
        {
            var (service, store, _, _) = Setup();
            var orcamento = await service.CriarOrcamentoAsync(new(Guid.NewGuid(), Guid.NewGuid(), 250m), CancellationToken.None);
            var sqs = new FakeSqs(); var config = Config();
            var services = new ServiceCollection();
            services.AddSingleton<IBillingStore>(store);
            services.AddSingleton<IEventosProcessadosStore>(new Processados());
            services.AddSingleton<IAlertSink>(new RecordingAlerts());
            services.AddScoped<CancelamentoOrcamentoService>();
            var provider = services.BuildServiceProvider();
            var processador = new ProcessadorComandos(provider.GetRequiredService<IServiceScopeFactory>(), sqs, config, NullLogger<ProcessadorComandos>.Instance, _ => TimeSpan.Zero);
            return (new ComandosConsumer(processador, sqs, config, NullLogger<ComandosConsumer>.Instance), sqs, store, orcamento);
        }

        private static string ComandoCancelar(Orcamento orcamento, string camposExtras = "") =>
            $$"""{"eventId":"{{Guid.NewGuid()}}","tipo":"cobranca.cancelar-orcamento.v1","ordemServicoId":"{{orcamento.OrdemServicoId}}","orcamentoId":"{{orcamento.Id}}"{{camposExtras}}}""";

        [Fact]
        public async Task AprovarOrcamentoGravaTraceparentETracestateDaActivityCorrenteNoOutbox()
        {
            var (service, store, _, _) = Setup();
            var orcamento = await service.CriarOrcamentoAsync(new(Guid.NewGuid(), Guid.NewGuid(), 250m), CancellationToken.None);
            using var corrente = IniciarAtividadeCorrente("fornecedor=valor");

            await service.AprovarOrcamentoAsync(orcamento.Id, orcamento.ClienteId, CancellationToken.None);

            var mensagem = Assert.Single(store.Mensagens);
            Assert.Equal("orcamento.aprovado.v1", mensagem.Tipo);
            Assert.Equal(ActivityIdFormat.W3C, corrente.IdFormat);
            Assert.Equal(corrente.Id, Campo(mensagem.Payload, "traceparent"));
            Assert.Equal("fornecedor=valor", Campo(mensagem.Payload, "tracestate"));
            Assert.Equal(orcamento.OrdemServicoId.ToString(), Campo(mensagem.Payload, "ordemServicoId"));
        }

        [Fact]
        public async Task ResultadoDoPagamentoEnviadoLevaOTraceparentDaActivityCorrenteERegistraSpanProducer()
        {
            var sqs = new FakeSqs();
            var publisher = new SqsResultPublisher(sqs, Config(), NullLogger<SqsResultPublisher>.Instance, new RecordingAlerts(), _ => TimeSpan.Zero);
            var evento = MensagensSaga.ResultadoPagamento(Guid.NewGuid(), Guid.NewGuid(), StatusPagamento.Capturado, "mp-1", DateTime.UtcNow);
            using var corrente = IniciarAtividadeCorrente("fornecedor=valor");

            await publisher.PublicarAsync(evento, CancellationToken.None);

            var corpo = Assert.Single(sqs.Delivered("main"));
            Assert.Equal("pagamento.resultado.v1", Campo(corpo, "tipo"));
            Assert.Equal(corrente.Id, Campo(corpo, "traceparent"));
            Assert.Equal("fornecedor=valor", Campo(corpo, "tracestate"));
            var span = Assert.Single(SpansDaMensageria(ActivityKind.Producer, s => s.TraceId == corrente.TraceId));
            Assert.Equal("publicar pagamento.resultado.v1", span.OperationName);
            Assert.Equal(corrente.SpanId, span.ParentSpanId);
            Assert.Same(corrente, Activity.Current);
        }

        [Fact]
        public async Task SemActivityCorrenteAMensagemEGravadaEEnviadaSemTraceparent()
        {
            var (service, store, _, _) = Setup();
            var orcamento = await service.CriarOrcamentoAsync(new(Guid.NewGuid(), Guid.NewGuid(), 250m), CancellationToken.None);
            var sqs = new FakeSqs();
            var publisher = new SqsResultPublisher(sqs, Config(), NullLogger<SqsResultPublisher>.Instance, new RecordingAlerts(), _ => TimeSpan.Zero);
            Assert.Null(Activity.Current);

            await service.AprovarOrcamentoAsync(orcamento.Id, orcamento.ClienteId, CancellationToken.None);
            await publisher.PublicarAsync(MensagensSaga.ResultadoPagamento(orcamento.OrdemServicoId, Guid.NewGuid(), StatusPagamento.Capturado, "mp-1", DateTime.UtcNow), CancellationToken.None);

            var mensagem = Assert.Single(store.Mensagens);
            using (var gravado = JsonDocument.Parse(mensagem.Payload))
            {
                Assert.False(gravado.RootElement.TryGetProperty("traceparent", out _));
                Assert.False(gravado.RootElement.TryGetProperty("tracestate", out _));
                Assert.Equal(orcamento.Id, gravado.RootElement.GetProperty("orcamentoId").GetGuid());
            }
            using var enviado = JsonDocument.Parse(Assert.Single(sqs.Delivered("main")));
            Assert.False(enviado.RootElement.TryGetProperty("traceparent", out _));
            Assert.False(enviado.RootElement.TryGetProperty("tracestate", out _));
        }

        [Fact]
        public async Task SqsEnvioRegistraSpanProducerComOTraceIdDoEnvelopeSemAlterarOCorpo()
        {
            var contexto = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded);
            var eventId = Guid.NewGuid(); var ordemServicoId = Guid.NewGuid();
            var payload = $$"""{"eventId":"{{eventId}}","tipo":"orcamento.aprovado.v1","ordemServicoId":"{{ordemServicoId}}","traceparent":"00-{{contexto.TraceId}}-{{contexto.SpanId}}-01"}""";
            var sqs = new FakeSqs();

            var envio = await SqsEnvio.EnviarAsync(sqs, new RecordingAlerts(), NullLogger.Instance, "main", "dlq", payload, eventId.ToString(), CancellationToken.None, _ => TimeSpan.Zero);

            Assert.Equal(ResultadoEnvio.Enviada, envio.Resultado);
            Assert.Equal(payload, Assert.Single(sqs.Delivered("main")));
            var span = Assert.Single(SpansDaMensageria(ActivityKind.Producer, s => s.TraceId == contexto.TraceId));
            Assert.Equal("publicar orcamento.aprovado.v1", span.OperationName);
            Assert.Equal(contexto.SpanId, span.ParentSpanId);
            Assert.Equal("aws_sqs", span.GetTagItem("messaging.system"));
            Assert.Equal(eventId.ToString(), span.GetTagItem("messaging.message.id"));
            Assert.Equal(ordemServicoId.ToString(), span.GetTagItem("ordem_servico.id"));
        }

        [Fact]
        public async Task ConsumirComandoComTraceparentValidoProcessaEmSpanConsumerFilhoDoEnvelope()
        {
            var (consumer, sqs, store, orcamento) = await MontarConsumidorAsync();
            var contexto = new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded);
            var corpo = ComandoCancelar(orcamento, $$""","traceparent":"00-{{contexto.TraceId}}-{{contexto.SpanId}}-01","tracestate":"fornecedor=valor" """.TrimEnd());

            await consumer.TratarAsync("cmd", new Message { Body = corpo, ReceiptHandle = "r-1", MessageId = "m-1" }, CancellationToken.None);

            var span = Assert.Single(SpansDaMensageria(ActivityKind.Consumer, s => s.TraceId == contexto.TraceId));
            Assert.Equal("processar cobranca.cancelar-orcamento.v1", span.OperationName);
            Assert.Equal(contexto.SpanId, span.ParentSpanId);
            Assert.True(span.HasRemoteParent);
            Assert.Equal(orcamento.OrdemServicoId.ToString(), span.GetTagItem("ordem_servico.id"));
            Assert.Equal(StatusOrcamento.Cancelado, orcamento.Status);
            Assert.Equal(["r-1"], sqs.Deleted);
            Assert.Equal(0, sqs.AttemptsTo("cmd-dlq"));
            // A resposta gerada pelo handler continua o mesmo trace, como filha do span de consumo.
            var resposta = Assert.Single(store.Mensagens);
            Assert.Equal("orcamento.cancelado.v1", resposta.Tipo);
            Assert.Equal(span.Id, Campo(resposta.Payload, "traceparent"));
            Assert.Equal("fornecedor=valor", Campo(resposta.Payload, "tracestate"));
        }

        [Theory]
        [InlineData("")]
        [InlineData(""","traceparent":"abc" """)]
        [InlineData(""","campoDesconhecido":{"a":[1,2]},"outro":42 """)]
        public async Task SemTraceparentValidoOComandoEProcessadoComoHojeEmSpanRaizSemIrParaADlq(string camposExtras)
        {
            var (consumer, sqs, store, orcamento) = await MontarConsumidorAsync();
            var ordem = orcamento.OrdemServicoId.ToString();
            // Activity ambiente (ex.: do laço de consumo): não pode virar pai do span da mensagem.
            using var ambiente = IniciarAtividadeCorrente();

            await consumer.TratarAsync("cmd", new Message { Body = ComandoCancelar(orcamento, camposExtras.TrimEnd()), ReceiptHandle = "r-1", MessageId = "m-1" }, CancellationToken.None);

            Assert.Equal(StatusOrcamento.Cancelado, orcamento.Status);
            Assert.Equal("orcamento.cancelado.v1", Assert.Single(store.Mensagens).Tipo);
            Assert.Equal(["r-1"], sqs.Deleted);
            Assert.Equal(0, sqs.AttemptsTo("cmd-dlq"));
            var span = Assert.Single(SpansDaMensageria(ActivityKind.Consumer, s => (string?)s.GetTagItem("ordem_servico.id") == ordem));
            Assert.Equal("processar cobranca.cancelar-orcamento.v1", span.OperationName);
            Assert.Equal(default, span.ParentSpanId);
            Assert.Null(span.ParentId);
            Assert.NotEqual(ambiente.TraceId, span.TraceId);
            Assert.Same(ambiente, Activity.Current);
        }

        [Fact]
        public void LeituraDoComandoIgnoraCamposDesconhecidosETraceparentInvalido()
        {
            var eventId = Guid.NewGuid(); var ordemServicoId = Guid.NewGuid(); var orcamentoId = Guid.NewGuid();
            var corpo = $$"""{"eventId":"{{eventId}}","tipo":"cobranca.cancelar-orcamento.v1","ordemServicoId":"{{ordemServicoId}}","orcamentoId":"{{orcamentoId}}","traceparent":"abc","tracestate":7,"campoDesconhecido":[1]}""";

            var comando = ComandoCancelamento.Ler(corpo);

            Assert.Equal(new ComandoCancelamento("cobranca.cancelar-orcamento.v1", eventId, null, ordemServicoId, orcamentoId), comando);
            using var documento = JsonDocument.Parse(corpo);
            Assert.Null(ContextoTrace.Extrair(documento.RootElement));
        }
    }
}
