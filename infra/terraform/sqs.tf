# Filas da Cobrança (a fila pertence ao consumidor).
# - comandos: comandos da Saga enviados pelo OS (Sqs:ComandosQueueUrl), com DLQ (Sqs:ComandosDeadLetterQueueUrl).
# - execucoes: execucao.concluida.v1 publicado pela Execução para a Cobrança (Sqs:BillingQueueUrl da Execução);
#   a Cobrança ainda não tem consumidor dessa fila, as mensagens expiram pela retenção.
# - saida-dlq: mensagens do outbox da Cobrança esgotadas (Sqs:DeadLetterQueueUrl), lidas pelo reprocessador da DLQ.
resource "aws_sqs_queue" "comandos_dlq" {
  name                      = "autoreparos-cobranca-comandos-dlq"
  message_retention_seconds = 1209600
  sqs_managed_sse_enabled   = true
}

resource "aws_sqs_queue" "comandos" {
  name                       = "autoreparos-cobranca-comandos"
  visibility_timeout_seconds = 90
  message_retention_seconds  = 345600
  receive_wait_time_seconds  = 20
  sqs_managed_sse_enabled    = true
  redrive_policy = jsonencode({
    deadLetterTargetArn = aws_sqs_queue.comandos_dlq.arn
    maxReceiveCount     = 5
  })
}

resource "aws_sqs_queue" "execucoes_dlq" {
  name                      = "autoreparos-cobranca-execucoes-dlq"
  message_retention_seconds = 1209600
  sqs_managed_sse_enabled   = true
}

resource "aws_sqs_queue" "execucoes" {
  name                       = "autoreparos-cobranca-execucoes"
  visibility_timeout_seconds = 90
  message_retention_seconds  = 345600
  receive_wait_time_seconds  = 20
  sqs_managed_sse_enabled    = true
  redrive_policy = jsonencode({
    deadLetterTargetArn = aws_sqs_queue.execucoes_dlq.arn
    maxReceiveCount     = 5
  })
}

resource "aws_sqs_queue" "saida_dlq" {
  name                      = "autoreparos-cobranca-saida-dlq"
  message_retention_seconds = 1209600
  sqs_managed_sse_enabled   = true
}
