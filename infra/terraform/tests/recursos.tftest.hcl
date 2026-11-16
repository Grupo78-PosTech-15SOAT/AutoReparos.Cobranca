# C11: ECR, filas de entrada com DLQ, role IRSA presa ao ServiceAccount cobranca/cobranca e segredo da database da Cobrança.
# Roda sem AWS: provider aws simulado e parâmetros SSM da plataforma substituídos por valores de exemplo.

mock_provider "aws" {
  mock_data "aws_caller_identity" {
    defaults = {
      account_id = "198903849859"
    }
  }
}

override_data {
  target = data.aws_ssm_parameter.oidc_provider_arn
  values = {
    insecure_value = "arn:aws:iam::198903849859:oidc-provider/oidc.eks.us-east-1.amazonaws.com/id/EXEMPLO"
  }
}

override_data {
  target = data.aws_ssm_parameter.oidc_provider_url
  values = {
    insecure_value = "oidc.eks.us-east-1.amazonaws.com/id/EXEMPLO"
  }
}

override_data {
  target = data.aws_ssm_parameter.database_address
  values = {
    insecure_value = "autoreparos-db.exemplo.us-east-1.rds.amazonaws.com"
  }
}

run "recursos_da_cobranca" {
  command = apply

  assert {
    condition     = aws_sqs_queue.comandos.name == "autoreparos-cobranca-comandos" && aws_sqs_queue.comandos_dlq.name == "autoreparos-cobranca-comandos-dlq"
    error_message = "A fila de entrada autoreparos-cobranca-comandos precisa da DLQ autoreparos-cobranca-comandos-dlq."
  }

  assert {
    condition     = jsondecode(aws_sqs_queue.comandos.redrive_policy).deadLetterTargetArn == aws_sqs_queue.comandos_dlq.arn && jsondecode(aws_sqs_queue.comandos.redrive_policy).maxReceiveCount > 0
    error_message = "A redrive_policy de autoreparos-cobranca-comandos precisa apontar para autoreparos-cobranca-comandos-dlq com maxReceiveCount."
  }

  assert {
    condition     = aws_sqs_queue.execucoes.name == "autoreparos-cobranca-execucoes" && aws_sqs_queue.execucoes_dlq.name == "autoreparos-cobranca-execucoes-dlq"
    error_message = "A fila de entrada autoreparos-cobranca-execucoes precisa da DLQ autoreparos-cobranca-execucoes-dlq."
  }

  assert {
    condition     = jsondecode(aws_sqs_queue.execucoes.redrive_policy).deadLetterTargetArn == aws_sqs_queue.execucoes_dlq.arn && jsondecode(aws_sqs_queue.execucoes.redrive_policy).maxReceiveCount > 0
    error_message = "A redrive_policy de autoreparos-cobranca-execucoes precisa apontar para autoreparos-cobranca-execucoes-dlq com maxReceiveCount."
  }

  assert {
    condition     = aws_sqs_queue.saida_dlq.name == "autoreparos-cobranca-saida-dlq"
    error_message = "A DLQ de saída precisa se chamar autoreparos-cobranca-saida-dlq."
  }

  assert {
    condition     = aws_ecr_repository.servico.name == "autoreparos-cobranca"
    error_message = "O ECR precisa se chamar autoreparos-cobranca."
  }

  assert {
    condition     = aws_iam_role.irsa.name == "autoreparos-cobranca-irsa"
    error_message = "A role IRSA precisa se chamar autoreparos-cobranca-irsa."
  }

  assert {
    condition     = aws_iam_role.irsa.permissions_boundary == "arn:aws:iam::198903849859:policy/autoreparos-servico-boundary"
    error_message = "A role IRSA precisa do permissions boundary autoreparos-servico-boundary."
  }

  assert {
    condition     = jsondecode(aws_iam_role.irsa.assume_role_policy).Statement[0].Principal.Federated == "arn:aws:iam::198903849859:oidc-provider/oidc.eks.us-east-1.amazonaws.com/id/EXEMPLO"
    error_message = "A trust da role IRSA precisa confiar no provedor OIDC do cluster lido do SSM."
  }

  assert {
    condition     = jsondecode(aws_iam_role.irsa.assume_role_policy).Statement[0].Condition.StringEquals["oidc.eks.us-east-1.amazonaws.com/id/EXEMPLO:sub"] == "system:serviceaccount:cobranca:cobranca"
    error_message = "A trust da role IRSA precisa ficar presa ao ServiceAccount cobranca do namespace cobranca."
  }

  assert {
    condition     = length(jsondecode(aws_iam_role.irsa.assume_role_policy).Statement) == 1
    error_message = "A trust da role IRSA não pode ter outras declarações."
  }

  assert {
    condition     = aws_secretsmanager_secret.database.name == "autoreparos/production/cobranca/database"
    error_message = "O segredo da database precisa se chamar autoreparos/production/cobranca/database."
  }

  assert {
    condition = (
      jsondecode(aws_secretsmanager_secret_version.database.secret_string).username == "cobranca_app" &&
      jsondecode(aws_secretsmanager_secret_version.database.secret_string).dbname == "cobranca_db" &&
      jsondecode(aws_secretsmanager_secret_version.database.secret_string).host == "autoreparos-db.exemplo.us-east-1.rds.amazonaws.com" &&
      jsondecode(aws_secretsmanager_secret_version.database.secret_string).port == 5432
    )
    error_message = "O segredo precisa de username cobranca_app, dbname cobranca_db, host do SSM e porta 5432."
  }

  assert {
    condition = strcontains(
      jsondecode(aws_secretsmanager_secret_version.database.secret_string).connectionString,
      "Host=autoreparos-db.exemplo.us-east-1.rds.amazonaws.com;Port=5432;Database=cobranca_db;Username=cobranca_app;Password=${random_password.database.result};SSL Mode=Prefer;"
    )
    error_message = "A connectionString do segredo precisa usar o host, a database, o usuário e a senha do próprio segredo."
  }
}
