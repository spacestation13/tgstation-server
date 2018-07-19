﻿using Microsoft.Extensions.Logging;
using System;
using Tgstation.Server.Host.Components.Chat.Commands;
using Tgstation.Server.Host.Core;

namespace Tgstation.Server.Host.Components.Chat
{
	/// <inheritdoc />
	sealed class ChatFactory : IChatFactory
	{
		/// <summary>
		/// The <see cref="IIOManager"/> for the <see cref="ChatFactory"/>
		/// </summary>
		readonly IIOManager ioManager;
		
		/// <summary>
		/// The <see cref="ILoggerFactory"/> for the <see cref="ChatFactory"/>
		/// </summary>
		readonly ILoggerFactory loggerFactory;

		/// <summary>
		/// The <see cref="ICommandFactory"/> for the <see cref="ChatFactory"/>
		/// </summary>
		readonly ICommandFactory commandFactory;

		/// <summary>
		/// Construct a <see cref="ChatFactory"/>
		/// </summary>
		/// <param name="ioManager">The value of <see cref="ioManager"/></param>
		/// <param name="loggerFactory">The value of <see cref="loggerFactory"/></param>
		/// <param name="commandFactory">The value of <see cref="commandFactory"/></param>
		public ChatFactory(IIOManager ioManager, ILoggerFactory loggerFactory, ICommandFactory commandFactory)
		{
			this.ioManager = ioManager ?? throw new ArgumentNullException(nameof(ioManager));
			this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
			this.commandFactory = commandFactory ?? throw new ArgumentNullException(nameof(commandFactory));
		}

		/// <inheritdoc />
		public IChat CreateChat() => new Chat(new ProviderFactory(), ioManager, loggerFactory.CreateLogger<Chat>(), commandFactory);
	}
}
