namespace P2ModLoader.Abstract;

public abstract class BaseTab(TabPage page) {
	protected readonly TabPage Tab = page;

	protected abstract void InitializeComponents();
}