# How to run

it's necessary to have installed .NET 9.0 SDK and MS SQL Server 2012 (or higher) to run nopCommerce.

cd /home/andre/Documents/UA/4ano/AS/nopCommerce/src/Presentation/Nop.Web
dotnet run

docker run --name nopcommerce_postgres -e POSTGRES_PASSWORD=nopCommerce_db_password -e POSTGRES_DB=nopcommerce -p 5432:5432 -d postgres:15


Task 1 Architecture Analysis:

Before writing a single line of instrumentation code, spend time understanding the codebase.
Your documentation must answer:
- How are the layers organised and what are the dependency rules between them?
Resposta: Dentro de src/Libraries existe uma pasta chamada Nop.Core, onde estão as entidades do domínio, e outra pasta chamada Nop.Services, onde estão os serviços que fazem a ligação entre o domínio e a aplicação. A pasta Nop.Web é a camada de apresentação, onde estão os controladores e as views. A camada de apresentação depende da camada de serviços, que por sua vez depende da camada de domínio.
- How does nopCommerce handle events internally — what is IEventPublisher and
how is it used?
Resposta: IEventPublisher é uma interface que define um método Publish, que é usado para publicar eventos dentro do sistema. Os eventos são usados para desacoplar as diferentes partes do sistema, permitindo que elas se comuniquem sem depender diretamente umas das outras. O IEventPublisher é usado em várias partes do código, como nos serviços e nos controladores, para publicar eventos quando algo importante acontece, como a criação de um novo produto ou a realização de um pedido.
- Where does the code make it easy to add observability, and where does it make it hard?
Resposta: O código torna fácil adicionar observabilidade em áreas onde os eventos são publicados, como nos serviços e nos controladores, pois é possível interceptar os eventos e adicionar instrumentação para monitorar o comportamento do sistema. No entanto, pode ser difícil adicionar observabilidade em áreas onde o código é mais acoplado ou onde não há uma clara separação de responsabilidades, como em algumas partes da camada de domínio, onde as entidades podem ter lógica de negócios complexa e interdependente.
- What would you need to change structurally to instrument it properly — and is that
change worth making?
Resposta: Para instrumentar o sistema adequadamente, seria necessário adicionar pontos de instrumentação em áreas-chave do código, como nos serviços e nos controladores, para monitorar eventos importantes e o comportamento do sistema. Isso poderia envolver a adição de código de instrumentação diretamente nos métodos que publicam eventos ou a criação de um mecanismo de interceptação para capturar os eventos sem modificar o código existente. A mudança estrutural necessária dependeria do nível de acoplamento e da complexidade do código, mas em geral, adicionar observabilidade é uma prática valiosa que pode ajudar a identificar problemas e melhorar o desempenho do sistema, então a mudança seria considerada válida.

This section is not a summary of the README. It is your architectural reading of the system.


Interface do publisher [`IEventPublisher`](/home/andre/Documents/UA/4ano/AS/nopCommerce/src/Libraries/Nop.Services/Events/IEventPublisher.cs)

publisher implentado por [`EventPublisher`](/home/andre/Documents/UA/4ano/AS/nopCommerce/src/Libraries/Nop.Services/Events/EventPublisher.cs)

depois existe 3 extensões de `IEventPublisher` para publicar eventos,mensagens e shipments, respectivamente.
