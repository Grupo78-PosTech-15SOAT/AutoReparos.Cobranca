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
{{- end }}
