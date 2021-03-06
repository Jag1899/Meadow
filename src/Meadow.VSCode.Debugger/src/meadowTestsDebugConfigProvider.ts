import * as vscode from 'vscode';
import * as Net from 'net';
import * as fs from 'fs';
import * as path from 'path';
import * as uuid from 'uuid/v1';
import * as dotnetLaunchDebug from './clrLaunchDebug';
import { Logger } from './logger';
import { ISolidityMeadowDebugConfig, IDebugAdapterExecutable, DEBUG_SESSION_ID, SOLIDITY_MEADOW_TYPE } from './constants';
import { resolveMeadowDebugAdapter } from './meadowDebugAdapter';
import * as common from './common';


export class MeadowTestsDebugConfigProvider implements vscode.DebugConfigurationProvider {

	private _server?: Net.Server;

	private _context: vscode.ExtensionContext;

	constructor(context: vscode.ExtensionContext) {
		this._context = context;
	}

	// Notice: this is working in latest stable vscode but is preview.
	provideDebugAdapter?(session: vscode.DebugSession, folder: vscode.WorkspaceFolder | undefined, executable: IDebugAdapterExecutable | undefined, config: vscode.DebugConfiguration, token?: vscode.CancellationToken): vscode.ProviderResult<IDebugAdapterExecutable> {
		
		let debugSessionID: string;

		if (config[DEBUG_SESSION_ID]) {
			debugSessionID = config[DEBUG_SESSION_ID];
		}
		else {
			// If the debug session ID is not already set then the CLR debugger has not been launched
			// so we will launch it.
			debugSessionID = uuid();
			dotnetLaunchDebug.launch(debugSessionID, config).catch(err => Logger.log("Error launching dotnet test", err));
		}

		return resolveMeadowDebugAdapter(this._context, debugSessionID, config);
	}

	provideDebugConfigurations(folder: vscode.WorkspaceFolder | undefined, token?: vscode.CancellationToken): vscode.ProviderResult<vscode.DebugConfiguration[]> {

		let configs: ISolidityMeadowDebugConfig[] = [
			{
				type: SOLIDITY_MEADOW_TYPE,
				request: "launch",
				name: "Debug Solidity (via unit test run)"
			}, {
				type: SOLIDITY_MEADOW_TYPE,
				request: "launch",
				withoutSolidityDebugging: true,
				name: "Debug Unit Tests (without Solidity debugging)"
			}];

		return configs;
	}

	/**
	 * Massage a debug configuration just before a debug session is being launched,
	 * e.g. add all missing attributes to the debug configuration.
	 */
	resolveDebugConfiguration(folder: vscode.WorkspaceFolder | undefined, config: vscode.DebugConfiguration, token?: vscode.CancellationToken): vscode.ProviderResult<vscode.DebugConfiguration> {

		Logger.log(`Resolving debug configuration: ${JSON.stringify(config)}`);

		let debugConfig = <ISolidityMeadowDebugConfig>config;
		
		if (debugConfig.withoutSolidityDebugging) {
			// TODO: use "dotnet test" to find and built assembly,
			// then return coreclr launch config for program
			throw new Error("TODO..");
		}

		let workspaceRoot = common.getWorkspaceFolder().uri.fsPath;

		common.validateDotnetVersion();

		let checksReady = false;
		if (checksReady) {

			// TODO: ensure a main .csproj file exists, if not prompt to setup
			let workspaceHasCsproj = false;
			if (!workspaceHasCsproj) {
				throw new Error("TODO..");
			}

			// TODO: if .csproj file exists, check that it references Meadow.UnitTestTemplate package (need to have this reference solcodegen).
			//		 if it doesn't, prompt to install nuget package
			let projMeadowPackagesOkay = false;
			if (!projMeadowPackagesOkay) {
				throw new Error("TODO..");
			}

		}
		
		debugConfig.workspaceDirectory = workspaceRoot;

		let pathKeys = ['debugAdapterFile', 'testAssembly', 'logFile'];
		common.expandConfigPath(workspaceRoot, debugConfig, pathKeys);

		Logger.log(`Using debug configuration: ${JSON.stringify(debugConfig)}`);

		return debugConfig;
	}

	dispose() {
		if (this._server) {
			this._server.close();
		}
	}
}