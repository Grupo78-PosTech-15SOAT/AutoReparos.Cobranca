# AutoReparos Cobrança

Cobrança is the isolated service for OS budgets, customer approval, Mercado Pago Checkout Pro, payment notifications, and authorized refunds. It owns its PostgreSQL schema and does not read the OS database.

## Arquitetura

- **Papel:** dono do orçamento aprovado pelo cliente, do pagamento (Mercado Pago Checkout Pro), do webhook de notificação e do reembolso autorizado. Não conhece o banco do OS.
- **Camadas:** `AutoReparos.Cobranca.Domain` (orçamento, pagamento, reembolso, outbox), `.Application` (`BillingService`, contratos), `.Infrastructure` (EF Core + Npgsql, consumidores e publicador SQS, cliente do Mercado Pago, `ReprocessadorDlq`) e `.Api` (Minimal API com JWT).
- **Banco:** PostgreSQL próprio (`ConnectionStrings__Billing`; `cobranca_db` na spec de bancos), com migrações aplicadas na inicialização.
- **Papel na Saga:** a Saga é orquestrada pelo OS Service; o Cobrança é participante e só fala por mensagens SQS. Publica `orcamento.aprovado.v1`, `pagamento.resultado.v1`, `orcamento.cancelado.v1` e `orcamento.cancelamento-recusado.v1` na fila de entrada da Saga e consome o comando `cobranca.cancelar-orcamento.v1` (compensação) na sua fila de comandos. Detalhes na seção "Contrato de mensagens da Saga (v1)".
- **Comunicação:** HTTP síncrono apenas para chamar o OS Service (confirmar a conclusão) e o Mercado Pago; o OS chama o Cobrança por `POST /api/v1/orcamentos`. A autenticação valida o JWT emitido pelo Cadastros.
- **Resiliência:** outbox transacional (`mensagens_saida`), deduplicação por `eventId` (`eventos_processados`), retry com backoff e DLQ, e reprocessamento administrativo em `POST /api/v1/admin/dlq/reprocessar`.

## Configuration

Set these values through environment variables or a secret manager:

| Setting | Purpose |
| --- | --- |
| `ConnectionStrings__Billing` | Cobrança PostgreSQL database connection |
| `Authentication__Authority` / `Authentication__Audience` | JWT issuer and audience |
| `MercadoPago__AccessToken` | Mercado Pago private access token |
| `MercadoPago__WebhookSecret` | Webhook signature secret |
| `OsService__BaseUrl` | OS Service base URL used to confirm completion |
| `OsService__BearerToken` | Service identity token authorized to query the OS API |
| `AWS_REGION` | AWS region for SQS |
| `Sqs__QueueUrl` / `Sqs__DeadLetterQueueUrl` | Entry queue of the OS Saga (result and budget events) and its dead-letter queue |
| `Sqs__ComandosQueueUrl` / `Sqs__ComandosDeadLetterQueueUrl` | Command queue consumed by Cobrança (`cobranca.cancelar-orcamento.v1`, plus legacy alias `billing.orcamento.cancelar.v1`) and its dead-letter queue |
| `Outbox__IntervaloMs` | Optional: polling interval of the outbox dispatcher in milliseconds (default 2000) |

The service applies EF Core migrations at startup. The Cobrança database and role must be provisioned separately with no access to OS tables. Never commit tokens or connection strings.

## Routes

- `POST /api/v1/orcamentos` receives an OS budget from the `ServicoOS` identity, or an administrator/receptionist.
- `POST /api/v1/orcamentos/{id}/aprovacao` records customer approval.
- `POST /api/v1/pagamentos/{id}/checkout` opens Checkout Pro only after approval and OS completion.
- `POST /api/v1/pagamentos/{id}/reembolsos` records an administrator-approved partial or full refund.
- `POST /api/v1/webhooks/mercado-pago` validates the signed notification and fetches the authoritative payment state from Mercado Pago.

Build and verify locally from this directory:

```sh
dotnet restore AutoReparos.Cobranca.slnx
dotnet build AutoReparos.Cobranca.slnx -m:1
dotnet test AutoReparos.Cobranca.Tests --no-build
dotnet format AutoReparos.Cobranca.slnx --verify-no-changes --no-restore
```

## Contrato de mensagens da Saga (v1)

O Cobrança participa da Saga orquestrada pelo OS Service apenas por mensagens SQS (JSON, chaves em camelCase). O contrato completo está em `.specs/features/fase4/saga-mensageria/contracts.md` do repositório principal; aqui fica o que o Cobrança publica e consome. Todo envelope tem `eventId`, `tipo`, `ordemServicoId` e `ocorreuEm` (UTC, ISO 8601); `correlacaoId` só aparece quando o Cobrança o conhece (nas respostas ao comando de cancelamento, ecoado do comando).

### Saída (fila de entrada da Saga, `Sqs__QueueUrl`)

| `tipo` | Quando | Campos extras |
| --- | --- | --- |
| `orcamento.aprovado.v1` | o cliente aprova o orçamento | `orcamentoId`, `clienteId` |
| `pagamento.resultado.v1` | webhook do Mercado Pago muda o estado do pagamento | `pagamentoId`, `status` (`Capturado`, `Rejeitado`, `Cancelado`, `Expirado`, `Reembolsado`), `referencia` |
| `orcamento.cancelado.v1` | comando de cancelamento aceito | `orcamentoId` |
| `orcamento.cancelamento-recusado.v1` | comando recusado ou inválido | `orcamentoId`, `motivo` |

- **Outbox.** As mensagens de `orcamento.aprovado.v1` e das respostas de cancelamento são gravadas na tabela `mensagens_saida` na mesma transação do fato de negócio. Um despachador em segundo plano as publica; ao iniciar ele retoma as pendentes. Cada mensagem tem 5 tentativas com backoff exponencial e jitter; ao esgotar, vai para `Sqs__DeadLetterQueueUrl`, é marcada como esgotada e gera o alerta `billing.sqs.exhausted` (`billing.sqs.dlq_failed` se nem a DLQ aceitar; nesse caso ela continua pendente). A aprovação HTTP responde 200 mesmo com o SQS fora do ar.
- **`pagamento.resultado.v1`** continua publicado direto pelo fluxo do webhook, com recuperação para frente: se a publicação falha, o webhook responde 503 `resultado_nao_publicado`, alerta, e a reentrega do Mercado Pago republica. O `eventId` é estável por pagamento + status + referência.
- O `eventId` é estável em reenvios; a Saga deduplica. A entrega é pelo menos uma vez.

### Entrada (fila de comandos, `Sqs__ComandosQueueUrl`)

| `tipo` | Campos | Efeito |
| --- | --- | --- |
| `cobranca.cancelar-orcamento.v1` (e alias legado de entrada `billing.orcamento.cancelar.v1`) | `eventId`, `ordemServicoId`, `orcamentoId`, `motivo`, `correlacaoId` | cancela o orçamento se `Pendente`, ou `Aprovado` sem pagamento iniciado; novo estado `Cancelado` |

- **Deduplicação.** O `eventId` processado é registrado em `eventos_processados` na mesma transação do efeito e da resposta; uma entrega repetida (inclusive concorrente) não repete o efeito nem a resposta.
- **Recusa.** Pagamento com checkout iniciado, capturado ou reembolsado, e orçamento `Recusado`, recusam com `motivo` `pagamento_existente` ou `orcamento_recusado`. Cancelar um orçamento já cancelado confirma de novo (idempotente). **Nenhum caminho de cancelamento chama estorno nem o provedor de pagamento.**
- **Mensagem inválida.** Tipo desconhecido, `eventId` ausente, orçamento inexistente ou `ordemServicoId` divergente geram alerta (`billing.comando.tipo_desconhecido`, `billing.comando.malformado`, `billing.comando.orcamento_inexistente`, `billing.comando.ordem_divergente`) e a resposta `orcamento.cancelamento-recusado.v1`. Sem `orcamentoId` e `ordemServicoId` válidos (ou JSON inválido) não há para quem responder: alerta `billing.comando.malformado` e DLQ de comandos.
- **Retry e DLQ.** Falha técnica no processamento usa 5 tentativas com backoff e jitter (um escopo novo por tentativa); ao esgotar, alerta `billing.comando.esgotado` e a mensagem vai para `Sqs__ComandosDeadLetterQueueUrl`. Se nem a DLQ aceitar, alerta `billing.comando.dlq_failed` e a mensagem não é apagada da fila (volta após o visibility timeout). Sem `Sqs__ComandosQueueUrl` o consumo fica desativado.

A migração `202611130001_AddMensageriaSaga` cria `mensagens_saida` e `eventos_processados` (escrita à mão, sem model snapshot, como as demais).

## CI/CD e configuração do repositório

O pipeline `.github/workflows/ci-cd.yml` roda em pull request para `main` e em push na `develop` e na `main`, com os jobs `build` (compila e confere `dotnet format`), `test` (testes de `AutoReparos.Cobranca.Tests`, cobertura mínima de linhas de 80%) e `quality` (SonarCloud).

Para o job `quality` passar, o repositório precisa destes itens em **Settings → Secrets and variables → Actions**:

| Nome | Onde criar | Obrigatório | Valor |
| --- | --- | --- | --- |
| `SONAR_TOKEN` | aba **Secrets** (do repositório ou da organização, com acesso a este repositório) | sim: sem ele o job `quality` falha de propósito | token gerado no SonarCloud em *My Account → Security*, por quem pode analisar os projetos da organização |
| `SONAR_ORGANIZATION` | aba **Variables** | não | padrão `grupo78-postech-15soat` |
| `SONAR_PROJECT_KEY` | aba **Variables** | não | padrão `Grupo78-PosTech-15SOAT_AutoReparos.Cobranca` |

O projeto `Grupo78-PosTech-15SOAT_AutoReparos.Cobranca` precisa existir no SonarCloud (ou o token precisa poder criá-lo). Nunca versione o token. Depois de criar o secret, reexecute o job `quality` da última execução; não é preciso novo commit.

Quando a proteção da `main` for aplicada (AD-026 do repositório pai), os jobs `build`, `test` e `quality` serão os status checks obrigatórios.

## Cobertura

O limite mínimo é de **80%** de cobertura de linhas. No CI (`.github/workflows/ci-cd.yml`) o job `test` roda os testes de `AutoReparos.Cobranca.Tests` com `XPlat Code Coverage` (formatos Cobertura e OpenCover) e o script `.github/scripts/verificar_cobertura.py` une os relatórios `coverage.cobertura.xml` e falha se o total ficar abaixo de `MINIMO_COBERTURA` (80). O job `quality` envia os relatórios OpenCover ao SonarCloud e espera o quality gate (migrações e `bin`/`obj` ficam fora da análise).

Relatório do SonarCloud: https://sonarcloud.io/project/overview?id=Grupo78-PosTech-15SOAT_AutoReparos.Cobranca
