using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AutoReparos.Cobranca.Application;

/// <summary>
/// Propagação do contexto de trace W3C pelos envelopes de mensagem (D-65): os campos opcionais <c>traceparent</c> e
/// <c>tracestate</c> ficam na raiz do envelope. Quem lê o envelope ignora campos desconhecidos.
/// </summary>
public static class ContextoTrace
{
    public const string NomeFonte = "AutoReparos.Cobranca.Mensageria";
    public static readonly ActivitySource Fonte = new(NomeFonte);

    /// <summary>Grava o contexto da <see cref="Activity.Current"/> (W3C) no envelope; sem Activity corrente, não grava nada.</summary>
    public static void Injetar(JsonObject envelope)
    {
        var atual = Activity.Current;
        if (atual is null || atual.IdFormat != ActivityIdFormat.W3C || atual.Id is null) return;
        envelope["traceparent"] = atual.Id;
        if (!string.IsNullOrEmpty(atual.TraceStateString)) envelope["tracestate"] = atual.TraceStateString;
    }

    /// <summary>Contexto remoto do envelope; ausente ou inválido devolve nulo (o consumidor abre um trace novo).</summary>
    public static ActivityContext? Extrair(JsonElement raiz)
    {
        if (raiz.ValueKind != JsonValueKind.Object || !raiz.TryGetProperty("traceparent", out var traceparent) || traceparent.ValueKind != JsonValueKind.String)
            return null;
        var tracestate = raiz.TryGetProperty("tracestate", out var estado) && estado.ValueKind == JsonValueKind.String ? estado.GetString() : null;
        return ActivityContext.TryParse(traceparent.GetString(), tracestate, isRemote: true, out var contexto) ? contexto : null;
    }

    /// <summary>
    /// Serializa o envelope com o contexto de trace corrente. O JSON sai igual ao de <c>JsonSerializer.Serialize</c>
    /// (sem passar por <c>JsonNode</c>, que reescaparia datas e textos); os campos de trace entram no fim do objeto.
    /// </summary>
    public static string Serializar<T>(T envelope, JsonSerializerOptions opcoes)
    {
        var json = JsonSerializer.Serialize(envelope, opcoes);
        var atual = Activity.Current;
        if (atual is null || atual.IdFormat != ActivityIdFormat.W3C || atual.Id is null || json.Length < 2 || json[^1] != '}') return json;
        var campos = $"\"traceparent\":{JsonSerializer.Serialize(atual.Id)}";
        if (!string.IsNullOrEmpty(atual.TraceStateString))
            campos += $",\"tracestate\":{JsonSerializer.Serialize(atual.TraceStateString)}";
        return string.Concat(json.AsSpan(0, json.Length - 1), json.Length == 2 ? "" : ",", campos, "}");
    }

    /// <summary>Inicia o span da mensagem (<c>publicar</c> no produtor, <c>processar</c> no consumidor) a partir do corpo do envelope.</summary>
    public static Activity? IniciarAtividade(string operacao, ActivityKind tipoAtividade, string corpo)
    {
        string tipo = "desconhecido"; string? eventId = null, ordemServicoId = null; ActivityContext? pai = null;
        try
        {
            using var documento = JsonDocument.Parse(corpo);
            var raiz = documento.RootElement;
            if (raiz.ValueKind == JsonValueKind.Object)
            {
                tipo = Texto(raiz, "tipo") ?? tipo; eventId = Texto(raiz, "eventId"); ordemServicoId = Texto(raiz, "ordemServicoId");
                pai = Extrair(raiz);
            }
        }
        catch (JsonException) { /* corpo inválido: span raiz sem atributos do envelope */ }
        // Sem contexto no envelope, o span é raiz: nenhuma Activity do laço de consumo/publicação vira pai.
        Activity.Current = null;
        var atividade = Fonte.StartActivity($"{operacao} {tipo}", tipoAtividade, pai ?? default);
        if (atividade is null) return null;
        atividade.SetTag("messaging.system", "aws_sqs");
        if (eventId is not null) atividade.SetTag("messaging.message.id", eventId);
        if (ordemServicoId is not null) atividade.SetTag("ordem_servico.id", ordemServicoId);
        return atividade;
    }

    private static string? Texto(JsonElement raiz, string nome) =>
        raiz.TryGetProperty(nome, out var valor) && valor.ValueKind == JsonValueKind.String ? valor.GetString() : null;
}
