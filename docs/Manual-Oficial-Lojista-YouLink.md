# Manual Oficial do Lojista — YouLink

> **Versão do Sistema:** YouLink v1.0.50 (Windows x64 e Android) • YouLink Admin v1.1.13  
> **Portal Central Oficial:** https://youlink-loja.onrender.com

---

## 1. Primeiros Passos e Instalação

O **YouLink** funciona no computador (**Windows x64**) e no celular (**Android**), mantendo seu catálogo, estoque e vendas organizados sem precisar programar nada.

### Como instalar no Windows
1. Acesse o portal oficial e clique em **Baixar YouLink Grátis** (`YouLink-Setup-x64.exe`).
2. Execute o instalador e siga os passos. O atalho **YouLink** será criado automaticamente na Área de Trabalho e no Menu Iniciar.
3. Nas próximas atualizações, basta aceitar o aviso de **Atualização Disponível** dentro do próprio aplicativo — seus produtos, contas e dados locais (`App_Data`) são preservados automaticamente.

### Como instalar no Android
1. Baixe `YouLink-Android.apk` pelo portal oficial.
2. Caso o Android peça permissão, habilite **Instalar apps de fontes desconhecidas** para o navegador.
3. Abra o aplicativo e entre com sua conta (ou crie uma conta gratuita).

---

## 2. Cadastro de Produtos, Variantes e Estoque

1. Abra o menu **Produtos** e clique em **+ Nova Categoria** para organizar sua vitrine (ex.: Roupas, Eletrônicos, Perfumes, Acessórios).
2. Clique em **+ Novo Produto**, informe nome, categoria, preço e estoque inicial.
3. No **Editor Completo do Produto**:
   - Adicione **fotos em alta qualidade** (com compressão inteligente para carregar rápido no celular dos clientes).
   - Configure **variantes independentes** (cor/modelo, tamanho/opção, SKU e quantidade em estoque).
   - Controle quando cada item aparece ou fica oculto na vitrine pública.
4. Use a tela **Estoque** para entradas, saídas rápidas e alertas automáticos de estoque baixo.

---

## 3. Estúdio IA Gemini (Anúncios, SEO e WhatsApp)

No cadastro de produtos e no **Editor do Produto**, você conta com o **Estúdio IA Gemini** integrado:

- **Gerar Kit de Vendas com IA:** com 1 clique, cria título otimizado, descrição persuasiva para a vitrine, texto pronto para enviar no WhatsApp/Status, legenda para Instagram com hashtags e chamada de banner.
- **Tom de voz configurável:** escolha entre *Luxo Editorial*, *Direto ao Ponto* ou *Promocional*.
- **Modo Gemini Online + Fallback Offline:** adicione sua chave gratuita do Google AI Studio (`GEMINI_API_KEY`) em **Configurações da Loja** (ou no servidor central) para usar o modelo **Gemini 4 Hydrogen**, ou utilize o assistente inteligente offline mesmo sem internet.

---

## 4. Publicação da Loja Online em 1 Clique

1. Abra **Configurações da Loja** e defina o nome da loja, WhatsApp de atendimento e logotipo.
2. No cartão **Colocar Loja no Ar**, clique em **Publicar minha loja**.
3. Seu endereço exclusivo (`/loja/sua-loja`) e o **QR Code oficial** ficam prontos na hora para colocar na bio do Instagram e enviar aos clientes.
4. Otimizações incluídas na vitrine:
   - Carregamento prioritário da imagem principal (LCP) e cache otimizado para celular;
   - Proteção anti-bot opcional via **Cloudflare Turnstile** no checkout e cadastro;
   - Instalação como aplicativo da loja (PWA) no celular do cliente.

---

## 5. Pagamentos: Pix Direto e Mercado Pago Automático

- **Pix Direto (Taxa Zero):** cadastre sua chave Pix e confirme os pedidos manualmente em **Pedidos**.
- **Mercado Pago OAuth (Automático):** clique em **Conectar Mercado Pago** nas Configurações da Loja. O cliente paga o Pix dinâmico ou cartão na vitrine e o sistema confirma o pedido e dá baixa no estoque automaticamente.
- **Frete e Entrega:** configure retirada local, entrega na mesma cidade/estado ou raio em quilômetros, além da estimativa PAC/SEDEX pelos Correios.

---

## 6. Planos, VIP, Gift Cards e Verificação Automática

| Plano | Principais Recursos |
| :--- | :--- |
| **Básico** | Vitrine Minimalista Luxo, catálogo online, estoque por variante e pedidos no WhatsApp. |
| **Intermediário (Meia Versão)** | 5 temas prontos alternáveis, vendas diretas por Pix na vitrine e relatório de vendas. |
| **Premium Sob Medida** | Estúdio Visual completo (cores, fontes, layout do hero, grade 2/3/4 colunas), vendas diretas, relatórios completos e personalização total. |

### Como funciona a ativação de VIP e Gift Cards
- **Gift Card:** digite o código em **Configurações da Loja** e clique em **Ativar Código Gift Card**.
- **VIP liberado pelo Admin ou Mercado Pago:** o aplicativo sincroniza automaticamente ao entrar, ao trocar de aba, ao voltar do segundo plano e em verificações periódicas, exibindo o popup de confirmação (**VIP Comprado** ou **VIP Cortesia**).
- **Verificação manual imediata:** se acabou de receber VIP, basta tocar em **Verificar VIP Agora** em Configurações da Loja para sincronizar e confirmar na hora com o servidor central.

---

## 7. Operação Avançada para o Dono da Plataforma (Admin)

- **Verificar Client / Verificar Todos os Clients:** na tela **Usuários & VIP** do YouLink Admin, audite o registro na nuvem, a vitrine publicada e o último check-in do aplicativo de cada lojista, com reparo automático de divergências (sempre preservando assinaturas pagas no Mercado Pago).
- **Configuração Remota e Telemetria:** avisos globais, feature flags (`remote_config.json`) e registro de falhas dos clientes ficam disponíveis via servidor central e prontos para integração com **Firebase Remote Config / Crashlytics**.
- **Infraestrutura 24h:** compatível com **Render**, **Google Cloud Run** (`deploy-google-cloud.ps1` / `scripts/deploy-google-cloud-run.sh`) e proxy de borda **Cloudflare Workers** (`infra/cloudflare-edge-worker/`) para cache acelerado e monitoramento contínuo de `/health`.
