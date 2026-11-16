output "ecr_url" {
  description = "URL do repositório ECR do serviço (a tag da imagem é o SHA do commit)."
  value       = aws_ecr_repository.servico.repository_url
}

output "irsa_role_arn" {
  description = "ARN da role IRSA anotada no ServiceAccount do chart."
  value       = aws_iam_role.irsa.arn
}

output "segredo_database_arn" {
  description = "ARN do segredo com a connection string do serviço."
  value       = aws_secretsmanager_secret.database.arn
}

output "fila_comandos_url" {
  description = "URL da fila de comandos da Saga."
  value       = aws_sqs_queue.comandos.url
}

output "fila_comandos_dlq_url" {
  description = "URL da DLQ da fila de comandos."
  value       = aws_sqs_queue.comandos_dlq.url
}

output "fila_execucoes_url" {
  description = "URL da fila em que a Execução publica as conclusões para a Cobrança."
  value       = aws_sqs_queue.execucoes.url
}

output "fila_saida_dlq_url" {
  description = "URL da DLQ das mensagens publicadas pela Cobrança."
  value       = aws_sqs_queue.saida_dlq.url
}
