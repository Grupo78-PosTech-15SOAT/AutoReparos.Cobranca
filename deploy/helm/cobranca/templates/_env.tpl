{{/* Configuração específica da Cobrança. */}}
{{- define "servico.env" -}}
- name: Authentication__Authority
  value: {{ .Values.autenticacao.emissor | quote }}
{{- with .Values.autenticacao.audiencia }}
- name: Authentication__Audience
  value: {{ . | quote }}
{{- end }}
- name: Telemetry__ServiceName
  value: {{ .Values.telemetria.nomeServico | quote }}
- name: Telemetry__OtlpEndpoint
  value: {{ .Values.telemetria.otlpEndpoint | quote }}
- name: Sqs__QueueUrl
  value: {{ include "servico.fila" (list . .Values.filas.saga) | quote }}
- name: Sqs__DeadLetterQueueUrl
  value: {{ include "servico.fila" (list . .Values.filas.saidaDlq) | quote }}
- name: Sqs__ComandosQueueUrl
  value: {{ include "servico.fila" (list . .Values.filas.comandos) | quote }}
- name: Sqs__ComandosDeadLetterQueueUrl
  value: {{ include "servico.fila" (list . .Values.filas.comandosDlq) | quote }}
- name: OsService__BaseUrl
  value: {{ .Values.integracoes.osBaseUrl | quote }}
- name: Execucao__BaseUrl
  value: {{ .Values.integracoes.execucaoBaseUrl | quote }}
# Token de serviço (D-58): o mesmo JWT do Cadastros autentica a Cobrança no OS e na Execução.
- name: OsService__BearerToken
  valueFrom:
    secretKeyRef:
      name: cobranca-segredos
      key: TokenServico
      optional: true
- name: Execucao__BearerToken
  valueFrom:
    secretKeyRef:
      name: cobranca-segredos
      key: TokenServico
      optional: true
# Credenciais do Mercado Pago; sem elas a Cobrança sobe e o checkout responde erro de integração.
- name: MercadoPago__AccessToken
  valueFrom:
    secretKeyRef:
      name: cobranca-segredos
      key: MercadoPagoAccessToken
      optional: true
- name: MercadoPago__WebhookSecret
  valueFrom:
    secretKeyRef:
      name: cobranca-segredos
      key: MercadoPagoWebhookSecret
      optional: true
{{- end }}
